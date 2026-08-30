using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Providers.TV;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Providers.Tests.TV;

public sealed class SeriesMetadataServiceTests : IDisposable
{
    private readonly Mock<ILibraryManager> _libraryManagerMock = new();
    private readonly Mock<IProviderManager> _providerManagerMock = new();
    private readonly Mock<IFileSystem> _fileSystemMock = new();
    private readonly Mock<IItemRepository> _itemRepositoryMock = new();
    private readonly Mock<ILocalizationManager> _localizationManagerMock = new();
    private readonly ILibraryManager? _previousLibraryManager;
    private readonly IProviderManager? _previousProviderManager;
    private readonly IFileSystem? _previousFileSystem;
    private readonly ILogger<BaseItem>? _previousLogger;

    public SeriesMetadataServiceTests()
    {
        _previousLibraryManager = BaseItem.LibraryManager;
        _previousProviderManager = BaseItem.ProviderManager;
        _previousFileSystem = BaseItem.FileSystem;
        _previousLogger = BaseItem.Logger;

        BaseItem.LibraryManager = _libraryManagerMock.Object;
        BaseItem.ProviderManager = _providerManagerMock.Object;
        BaseItem.FileSystem = _fileSystemMock.Object;
        BaseItem.Logger = NullLogger<BaseItem>.Instance;
    }

    [Fact]
    public async Task AfterMetadataRefresh_PreRefreshSuppression_SkipsEpisodeSeasonLinking()
    {
        var (service, series, _, episode) = CreateServiceWithFlatEpisode();

        using (SeriesMetadataRefreshScope.SuppressChildReconciliation())
        {
            await service.InvokeAfterMetadataRefresh(series);
        }

        Assert.Null(episode.ParentIndexNumber);
        Assert.Equal(Guid.Empty, episode.SeasonId);
        Assert.Null(episode.SeasonName);

        _libraryManagerMock.Verify(i => i.FillMissingEpisodeNumbersFromPath(It.IsAny<Episode>(), It.IsAny<bool>()), Times.Never);
        _libraryManagerMock.Verify(i => i.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AfterMetadataRefresh_FinalRefresh_LinksEpisodeToExistingSeason()
    {
        var (service, series, season, episode) = CreateServiceWithFlatEpisode();

        await service.InvokeAfterMetadataRefresh(series);

        Assert.Equal(1, episode.ParentIndexNumber);
        Assert.Equal(season.Id, episode.SeasonId);
        Assert.Equal(season.Name, episode.SeasonName);

        _libraryManagerMock.Verify(i => i.FillMissingEpisodeNumbersFromPath(episode, false), Times.Once);
        _libraryManagerMock.Verify(i => i.UpdateItemAsync(episode, series, ItemUpdateType.MetadataImport, It.IsAny<CancellationToken>()), Times.Once);
    }

    public void Dispose()
    {
        BaseItem.LibraryManager = _previousLibraryManager!;
        BaseItem.ProviderManager = _previousProviderManager!;
        BaseItem.FileSystem = _previousFileSystem!;
        BaseItem.Logger = _previousLogger!;
    }

    private (TestSeriesMetadataService Service, Series Series, Season Season, Episode Episode) CreateServiceWithFlatEpisode()
    {
        var libraryOptions = new LibraryOptions
        {
            EnableAutomaticSeriesGrouping = false
        };

        _libraryManagerMock.Reset();
        _libraryManagerMock.Setup(i => i.GetLibraryOptions(It.IsAny<BaseItem>())).Returns(libraryOptions);
        _libraryManagerMock
            .Setup(i => i.FillMissingEpisodeNumbersFromPath(It.IsAny<Episode>(), false))
            .Callback<Episode, bool>((episode, _) => episode.ParentIndexNumber = 1)
            .Returns(true);
        _libraryManagerMock
            .Setup(i => i.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var series = new Series
        {
            Id = Guid.NewGuid(),
            Name = "Battlestar Galactica",
            PresentationUniqueKey = "series-key"
        };

        var season = new Season
        {
            Id = Guid.NewGuid(),
            Name = "Season 1",
            IndexNumber = 1,
            Path = @"F:\Media\Battlestar Galactica\Season 1",
            SeriesId = series.Id,
            SeriesName = series.Name,
            SeriesPresentationUniqueKey = series.PresentationUniqueKey
        };

        var episode = new Episode
        {
            Id = Guid.NewGuid(),
            Name = "Part 1",
            Path = @"F:\Media\Battlestar Galactica\Part 1.mkv",
            SeriesId = series.Id,
            SeriesName = series.Name
        };

        season.SetParent(series);
        season.Children = Array.Empty<BaseItem>();
        episode.SetParent(series);
        series.Children = new BaseItem[] { season, episode };

        _libraryManagerMock
            .Setup(i => i.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(Array.Empty<BaseItem>());
        _libraryManagerMock
            .Setup(i => i.GetItemById(series.Id))
            .Returns(series);
        _libraryManagerMock
            .Setup(i => i.GetItemById(season.Id))
            .Returns(season);

        var service = new TestSeriesMetadataService(
            _providerManagerMock.Object,
            _fileSystemMock.Object,
            _libraryManagerMock.Object,
            _localizationManagerMock.Object,
            _itemRepositoryMock.Object);

        return (service, series, season, episode);
    }

    private sealed class TestSeriesMetadataService : SeriesMetadataService
    {
        public TestSeriesMetadataService(
            IProviderManager providerManager,
            IFileSystem fileSystem,
            ILibraryManager libraryManager,
            ILocalizationManager localizationManager,
            IItemRepository itemRepository)
            : base(
                Mock.Of<IServerConfigurationManager>(),
                NullLogger<SeriesMetadataService>.Instance,
                providerManager,
                fileSystem,
                libraryManager,
                localizationManager,
                Mock.Of<IExternalDataManager>(),
                itemRepository)
        {
        }

        public Task InvokeAfterMetadataRefresh(Series series)
        {
            var options = new MetadataRefreshOptions(Mock.Of<IDirectoryService>())
            {
                MetadataRefreshMode = MetadataRefreshMode.None
            };

            return AfterMetadataRefresh(series, options, CancellationToken.None);
        }
    }
}
