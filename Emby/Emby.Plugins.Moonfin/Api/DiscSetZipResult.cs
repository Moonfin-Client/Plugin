using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Services;
using Moonfin.Games;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>
    /// Sends a PlayStation disc set as an uncompressed zip written as it goes out, so there's no
    /// size limit and every track arrives.
    /// </summary>
    internal sealed class DiscSetZipResult : IAsyncStreamWriter
    {
        private readonly DiscSet _discSet;
        private readonly string? _deleteAfter;

        /// <param name="deleteAfter">A temp file the set reads from, deleted once the zip is sent.</param>
        public DiscSetZipResult(DiscSet discSet, string? deleteAfter = null)
        {
            _discSet = discSet;
            _deleteAfter = deleteAfter;
        }

        public async Task WriteToAsync(IResponse response, CancellationToken cancellationToken)
        {
            response.ContentType = "application/zip";
            response.SetContentLength(_discSet.Length);

            // The zip is written synchronously, so it runs off the request thread.
            var output = response.OutputWriter.AsStream(leaveOpen: true);
            try
            {
                await Task.Run(() => _discSet.WriteTo(output), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (_deleteAfter != null)
                {
                    try { File.Delete(_deleteAfter); }
                    catch (IOException)
                    {
                        // A temp file left behind is cleared with the rest of the temp folder.
                    }
                }
            }
        }
    }
}
