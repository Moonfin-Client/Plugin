using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Emby.Plugins.Moonfin.Api;
using Emby.Plugins.Moonfin.Models;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Moonfin.CustomRows;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests
{
    public sealed class LocalItemIdServiceTests : IDisposable
    {
        private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "moonfin-emby-local-ids-" + Guid.NewGuid().ToString("N"));
        private readonly CustomRowCacheService _customRows;
        private readonly ImdbListsCacheService _imdb;
        private readonly LocalItemIdService _service;
        private long _nextId = 100;

        public LocalItemIdServiceTests()
        {
            Directory.CreateDirectory(_dataPath);
            var logger = AdminDefaultsMergeTests.NoOpLogger.Create();
            _customRows = new CustomRowCacheService(logger, _dataPath);
            _imdb = new ImdbListsCacheService(logger, _dataPath);

            // None of these tests reach the library: the matching is checked on candidates handed
            // in directly, and an arrival only asks the library about entries already matched.
            _service = new LocalItemIdService(null!, _customRows, _imdb, logger);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dataPath, recursive: true); }
            catch (IOException) { }
        }

        [Fact]
        public void TheExactMatchBeatsALooseCandidate()
        {
            var loose = Movie(tmdb: "6030");
            var exact = Movie(tmdb: "603");

            Assert.Equal(LocalItemIdService.IdOf(exact), LocalItemIdService.Best(new TitleIds(false, "603", null, null), new BaseItem[] { loose, exact }));
        }

        [Fact]
        public void TheTmdbMatchBeatsAnImdbOne()
        {
            var imdbOnly = Movie(tmdb: "1", imdb: "tt0133093");
            var tmdb = Movie(tmdb: "603", imdb: "tt9999999");

            Assert.Equal(LocalItemIdService.IdOf(tmdb), LocalItemIdService.Best(new TitleIds(false, "603", "tt0133093", null), new BaseItem[] { imdbOnly, tmdb }));
        }

        [Fact]
        public void AMovieNeverMatchesASeriesWithTheSameTmdbNumber() =>
            Assert.Null(LocalItemIdService.Best(new TitleIds(false, "1399", null, null), new BaseItem[] { Series(tmdb: "1399") }));

        [Fact]
        public void WithoutTheOptInTheResponseCarriesNoIdsAndTheCacheKeepsThem()
        {
            var cached = ListMovie(tmdb: "603");
            cached.LocalItemId = "123";
            var items = new List<CustomRowItem> { cached, ListMovie(tmdb: "604") };

            var response = _service.ForResponse(items, null, includeLocalItemIds: false);

            Assert.All(response, item => Assert.Null(item.LocalItemId));
            Assert.Equal("123", cached.LocalItemId);
            Assert.DoesNotContain("localItemId", JsonSerializer.Serialize(response, MoonfinJson.Options));
        }

        [Fact]
        public void ARowWithoutMatchesIsReturnedAsItIs()
        {
            var items = new List<CustomRowItem> { ListMovie(tmdb: "603") };

            Assert.Same(items, _service.ForResponse(items, null, includeLocalItemIds: false));
            Assert.Same(items, _service.ForResponse(items, null, includeLocalItemIds: true));
        }

        [Fact]
        public void AMissingUserSeesNoMatches()
        {
            var cached = ListMovie(tmdb: "603");
            cached.LocalItemId = "123";

            var response = _service.ForResponse(new List<CustomRowItem> { cached }, null, includeLocalItemIds: true);

            Assert.Null(Assert.Single(response).LocalItemId);
        }

        [Fact]
        public void TheIdIsOnTheWireOnlyWhenSet()
        {
            var item = ListMovie(tmdb: "603");
            Assert.DoesNotContain("localItemId", JsonSerializer.Serialize(item, MoonfinJson.Options));

            item.LocalItemId = "123";
            Assert.Contains("\"localItemId\":\"123\"", JsonSerializer.Serialize(item, MoonfinJson.Options));
        }

        [Fact]
        public void AnArrivalFillsTheCachedEntriesThatLackIt()
        {
            var row = ListMovie(tmdb: "603");
            var chart = ListMovie(imdb: "tt0133093");
            var otherKind = new CustomRowItem { Type = "Series", ProviderIds = new CustomRowItemProviderIds { Tmdb = "603" } };
            _customRows.Set("row", new List<CustomRowItem> { row, otherKind });
            _imdb.SetItems("imdb_top_250_movies", new List<CustomRowItem> { chart });

            var arrival = Movie(tmdb: "603", imdb: "tt0133093");

            Assert.Equal(2, _service.MatchArrival(arrival));
            Assert.Equal(LocalItemIdService.IdOf(arrival), row.LocalItemId);
            Assert.Equal(LocalItemIdService.IdOf(arrival), chart.LocalItemId);
            Assert.Null(otherKind.LocalItemId);
        }

        [Fact]
        public void ArrivalsOtherThanMoviesAndSeriesAreIgnored()
        {
            _customRows.Set("row", new List<CustomRowItem> { ListMovie(tmdb: "603") });
            var episode = new Episode { InternalId = 7 };
            episode.ProviderIds["Tmdb"] = "603";

            Assert.Equal(0, _service.MatchArrival(episode));
        }

        private Movie Movie(string? tmdb = null, string? imdb = null)
        {
            var movie = new Movie { InternalId = _nextId++ };
            if (tmdb != null) movie.ProviderIds["Tmdb"] = tmdb;
            if (imdb != null) movie.ProviderIds["Imdb"] = imdb;
            return movie;
        }

        private Series Series(string? tmdb = null)
        {
            var series = new Series { InternalId = _nextId++ };
            if (tmdb != null) series.ProviderIds["Tmdb"] = tmdb;
            return series;
        }

        private static CustomRowItem ListMovie(string? tmdb = null, string? imdb = null) => new CustomRowItem
        {
            Name = "Title",
            Type = "Movie",
            ProviderIds = new CustomRowItemProviderIds { Tmdb = tmdb, Imdb = imdb }
        };
    }
}
