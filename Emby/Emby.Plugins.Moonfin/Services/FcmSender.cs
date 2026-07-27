using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Sends push through the FCM HTTP v1 API. Auth is hand-rolled: a service account signs a JWT
    /// which is exchanged for a short-lived OAuth2 access token, cached in memory until shortly
    /// before it expires.
    ///
    /// netstandard2.1 note: RSA.ImportFromPem and ImportPkcs8PrivateKey are not on the
    /// netstandard2.1 compile surface. Reflection can't call ImportPkcs8PrivateKey either. It takes
    /// a ReadOnlySpan&lt;byte&gt; that can't be boxed for Invoke. So the private key is decoded from
    /// PEM into RSAParameters by a tiny DER parser and loaded via ImportParameters, which IS
    /// available here.
    /// </summary>
    public class FcmSender
    {
        private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";
        private const string DefaultTokenUri = "https://oauth2.googleapis.com/token";

        // Must match the channel the app creates at startup, otherwise pushes
        // land in the generic channel Android makes up and lose their importance.
        private const string AndroidChannelId = "seerr_notifications";

        private readonly ILogger _logger;

        private readonly SemaphoreSlim _tokenLock = new SemaphoreSlim(1, 1);
        private string? _cachedToken;
        private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;
        private string? _tokenAccountEmail;

        public FcmSender(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<FcmSendResult> SendAsync(
            string deviceToken,
            string title,
            string body,
            string route,
            string? requestId = null,
            string? platform = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(deviceToken))
                return FcmSendResult.Failed;

            var account = LoadServiceAccount();
            if (account == null)
                return FcmSendResult.Failed;

            string accessToken;
            try
            {
                accessToken = await GetAccessTokenAsync(account, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warn("Failed to mint FCM access token: " + ex.Message);
                return FcmSendResult.Failed;
            }

            object payload;
            var isRequest = !string.IsNullOrEmpty(requestId);
            var isIos = string.Equals(platform, "ios", StringComparison.OrdinalIgnoreCase);

            if (isRequest && isIos)
            {
                payload = new
                {
                    message = new
                    {
                        token = deviceToken,
                        notification = new { title, body },
                        data = new { route, requestId = requestId!, kind = "request" },
                        android = new { priority = "high", notification = new { channel_id = AndroidChannelId } },
                        apns = new
                        {
                            headers = new Dictionary<string, string> { ["apns-priority"] = "10" },
                            payload = new { aps = new { sound = "default", category = "seerr_request" } }
                        }
                    }
                };
            }
            else if (isRequest)
            {
                payload = new
                {
                    message = new
                    {
                        token = deviceToken,
                        notification = new { title, body },
                        data = new { route },
                        android = new { priority = "high", notification = new { channel_id = AndroidChannelId } }
                    }
                };
            }
            else
            {
                payload = new
                {
                    message = new
                    {
                        token = deviceToken,
                        notification = new { title, body },
                        data = new { route },
                        android = new { priority = "high", notification = new { channel_id = AndroidChannelId } },
                        apns = new
                        {
                            headers = new Dictionary<string, string> { ["apns-priority"] = "10" },
                            payload = new { aps = new { sound = "default" } }
                        }
                    }
                };
            }

            var url = $"https://fcm.googleapis.com/v1/projects/{account.ProjectId}/messages:send";

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
                request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return FcmSendResult.Ok;

                var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (IsTokenDead(response.StatusCode, responseBody))
                    return FcmSendResult.TokenDead;

                _logger.Warn("FCM send failed with status " + (int)response.StatusCode + ": " + ExtractErrorStatus(responseBody));
                return FcmSendResult.Failed;
            }
            catch (Exception ex)
            {
                _logger.Warn("FCM send request failed: " + ex.Message);
                return FcmSendResult.Failed;
            }
        }

        private ServiceAccount? LoadServiceAccount()
        {
            var json = Plugin.Instance?.Configuration?.GetFcmServiceAccountJson();
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var clientEmail = GetString(root, "client_email");
                var privateKey = GetString(root, "private_key");
                var projectId = GetString(root, "project_id");
                var tokenUri = GetString(root, "token_uri") ?? DefaultTokenUri;

                if (string.IsNullOrEmpty(clientEmail) || string.IsNullOrEmpty(privateKey) || string.IsNullOrEmpty(projectId))
                {
                    _logger.Info("FCM service account is missing required fields; push disabled", 0);
                    return null;
                }

                return new ServiceAccount(clientEmail!, privateKey!, projectId!, tokenUri!);
            }
            catch (Exception ex)
            {
                _logger.Info("FCM service account JSON could not be parsed; push disabled: " + ex.Message, 0);
                return null;
            }
        }

        private async Task<string> GetAccessTokenAsync(ServiceAccount account, CancellationToken cancellationToken)
        {
            if (_cachedToken != null && _tokenAccountEmail == account.ClientEmail &&
                DateTimeOffset.UtcNow < _tokenExpiry - TimeSpan.FromMinutes(5))
                return _cachedToken;

            await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_cachedToken != null && _tokenAccountEmail == account.ClientEmail &&
                    DateTimeOffset.UtcNow < _tokenExpiry - TimeSpan.FromMinutes(5))
                    return _cachedToken;

                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var jwt = BuildSignedJwt(account, now);

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var form = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"),
                    new KeyValuePair<string, string>("assertion", jwt)
                });

                using var response = await client.PostAsync(account.TokenUri, form, cancellationToken).ConfigureAwait(false);
                var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Token endpoint returned {(int)response.StatusCode}");

                using var doc = JsonDocument.Parse(responseBody);
                var accessToken = GetString(doc.RootElement, "access_token");
                var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var exp) && exp.ValueKind == JsonValueKind.Number
                    ? exp.GetInt32()
                    : 3600;

                if (string.IsNullOrEmpty(accessToken))
                    throw new InvalidOperationException("Token endpoint returned no access token");

                _cachedToken = accessToken;
                _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
                _tokenAccountEmail = account.ClientEmail;
                return accessToken!;
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        private static string BuildSignedJwt(ServiceAccount account, long now)
        {
            var header = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT" })));

            var claims = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                iss = account.ClientEmail,
                scope = Scope,
                aud = account.TokenUri,
                iat = now,
                exp = now + 3600
            })));

            var signingInput = header + "." + claims;

            using var rsa = RSA.Create();
            rsa.ImportParameters(RsaKeyImport.FromPem(account.PrivateKey));
            var signature = rsa.SignData(Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            return signingInput + "." + Base64Url(signature);
        }

        private static bool IsTokenDead(HttpStatusCode status, string responseBody)
        {
            if (status == HttpStatusCode.NotFound)
                return true;

            var errorStatus = ExtractErrorStatus(responseBody);
            return string.Equals(errorStatus, "UNREGISTERED", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(errorStatus, "NOT_FOUND", StringComparison.OrdinalIgnoreCase);
        }

        private static string? ExtractErrorStatus(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody)) return null;

            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("error", out var error) &&
                    error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("status", out var statusProp))
                    return statusProp.GetString();
            }
            catch { /* Non-JSON body; nothing to extract. */ }

            return null;
        }

        private static string? GetString(JsonElement element, string name)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(name, out var prop) &&
                prop.ValueKind == JsonValueKind.String)
                return prop.GetString();

            return null;
        }

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private sealed class ServiceAccount
        {
            public ServiceAccount(string clientEmail, string privateKey, string projectId, string tokenUri)
            {
                ClientEmail = clientEmail;
                PrivateKey = privateKey;
                ProjectId = projectId;
                TokenUri = tokenUri;
            }

            public string ClientEmail { get; }
            public string PrivateKey { get; }
            public string ProjectId { get; }
            public string TokenUri { get; }
        }
    }

    /// <summary>Outcome of an FCM send, used by the caller to prune dead tokens.</summary>
    public enum FcmSendResult
    {
        Ok,
        TokenDead,
        Failed
    }

    /// <summary>
    /// Decodes a PEM-encoded RSA private key into <see cref="RSAParameters"/> using a minimal
    /// ASN.1/DER reader. Needed because RSA.ImportFromPem / ImportPkcs8PrivateKey are absent from
    /// the netstandard2.1 compile surface. Handles both PKCS#8 ("BEGIN PRIVATE KEY", the format
    /// Google service-account keys use) and PKCS#1 ("BEGIN RSA PRIVATE KEY").
    /// </summary>
    internal static class RsaKeyImport
    {
        public static RSAParameters FromPem(string pem)
        {
            var der = DecodePem(pem);
            return pem.IndexOf("BEGIN RSA PRIVATE KEY", StringComparison.Ordinal) >= 0
                ? ParsePkcs1(der)
                : ParsePkcs8(der);
        }

        private static byte[] DecodePem(string pem)
        {
            var sb = new StringBuilder();
            foreach (var rawLine in pem.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("-----", StringComparison.Ordinal)) continue;
                sb.Append(line);
            }
            return Convert.FromBase64String(sb.ToString());
        }

        // PrivateKeyInfo ::= SEQUENCE { version INTEGER, algorithm SEQUENCE, privateKey OCTET STRING }
        // where privateKey is a DER-encoded PKCS#1 RSAPrivateKey.
        private static RSAParameters ParsePkcs8(byte[] der)
        {
            var reader = new DerReader(der);
            reader.ReadSequenceHeader();
            reader.ReadInteger();          // version
            reader.SkipSequence();         // AlgorithmIdentifier
            var inner = reader.ReadOctetString();
            return ParsePkcs1(inner);
        }

        // RSAPrivateKey ::= SEQUENCE { version, n, e, d, p, q, dp, dq, iq }
        private static RSAParameters ParsePkcs1(byte[] der)
        {
            var reader = new DerReader(der);
            reader.ReadSequenceHeader();
            reader.ReadInteger();          // version
            var n = reader.ReadInteger();
            var e = reader.ReadInteger();
            var d = reader.ReadInteger();
            var p = reader.ReadInteger();
            var q = reader.ReadInteger();
            var dp = reader.ReadInteger();
            var dq = reader.ReadInteger();
            var iq = reader.ReadInteger();

            var modulus = TrimLeadingZeros(n);
            var modLen = modulus.Length;
            var half = (modLen + 1) / 2;

            return new RSAParameters
            {
                Modulus = modulus,
                Exponent = TrimLeadingZeros(e),
                D = Align(d, modLen),
                P = Align(p, half),
                Q = Align(q, half),
                DP = Align(dp, half),
                DQ = Align(dq, half),
                InverseQ = Align(iq, half)
            };
        }

        private static byte[] TrimLeadingZeros(byte[] value)
        {
            var start = 0;
            while (start < value.Length - 1 && value[start] == 0) start++;
            if (start == 0) return value;
            var trimmed = new byte[value.Length - start];
            Array.Copy(value, start, trimmed, 0, trimmed.Length);
            return trimmed;
        }

        // Left-pads with zeros (or trims leading zeros) to exactly length bytes, as some RSA
        // implementations require the CRT parameters to be a fixed size.
        private static byte[] Align(byte[] value, int length)
        {
            value = TrimLeadingZeros(value);
            if (value.Length == length) return value;
            var result = new byte[length];
            if (value.Length < length)
                Array.Copy(value, 0, result, length - value.Length, value.Length);
            else
                Array.Copy(value, value.Length - length, result, 0, length);
            return result;
        }

        private sealed class DerReader
        {
            private readonly byte[] _data;
            private int _pos;

            public DerReader(byte[] data) { _data = data; }

            public void ReadSequenceHeader() => ExpectTagAndLength(0x30);

            public void SkipSequence()
            {
                var len = ExpectTagAndLength(0x30);
                _pos += len;
            }

            public byte[] ReadInteger()
            {
                var len = ExpectTagAndLength(0x02);
                var value = new byte[len];
                Array.Copy(_data, _pos, value, 0, len);
                _pos += len;
                return value;
            }

            public byte[] ReadOctetString()
            {
                var len = ExpectTagAndLength(0x04);
                var value = new byte[len];
                Array.Copy(_data, _pos, value, 0, len);
                _pos += len;
                return value;
            }

            private int ExpectTagAndLength(byte expectedTag)
            {
                if (_pos >= _data.Length || _data[_pos] != expectedTag)
                    throw new FormatException($"DER: expected tag 0x{expectedTag:X2} at offset {_pos}");
                _pos++;
                return ReadLength();
            }

            private int ReadLength()
            {
                int first = _data[_pos++];
                if ((first & 0x80) == 0) return first;

                var numBytes = first & 0x7F;
                if (numBytes == 0 || numBytes > 4)
                    throw new FormatException("DER: unsupported length encoding");

                int len = 0;
                for (var i = 0; i < numBytes; i++)
                    len = (len << 8) | _data[_pos++];
                return len;
            }
        }
    }
}
