using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events;
using MediaBrowser.Controller.BaseItemManager;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tvdb.Sdk;
using Episode = MediaBrowser.Controller.Entities.TV.Episode;
using Season = MediaBrowser.Controller.Entities.TV.Season;
using Series = MediaBrowser.Controller.Entities.TV.Series;

namespace Jellyfin.Plugin.Tvdb.Providers
{
    /// <summary>
    /// Tvdb Missing Episode provider.
    /// </summary>
    public class TvdbMissingEpisodeProvider : IHostedService
    {
        /// <summary>
        /// The provider name.
        /// </summary>
        public static readonly string ProviderName = "Missing Episode Fetcher";

        private readonly TvdbClientManager _tvdbClientManager;
        private readonly IBaseItemManager _baseItemManager;
        private readonly IProviderManager _providerManager;
        private readonly ILocalizationManager _localization;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<TvdbMissingEpisodeProvider> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="TvdbMissingEpisodeProvider"/> class.
        /// </summary>
        /// <param name="tvdbClientManager">Instance of the <see cref="TvdbClientManager"/> class.</param>
        /// <param name="baseItemManager">Instance of the <see cref="IBaseItemManager"/> interface.</param>
        /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
        /// <param name="localization">Instance of the <see cref="ILocalizationManager"/> interface.</param>
        /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
        /// <param name="logger">Instance of the <see cref="ILogger{TvdbMissingEpisodeProvider}"/> interface.</param>
        public TvdbMissingEpisodeProvider(
            TvdbClientManager tvdbClientManager,
            IBaseItemManager baseItemManager,
            IProviderManager providerManager,
            ILocalizationManager localization,
            ILibraryManager libraryManager,
            ILogger<TvdbMissingEpisodeProvider> logger)
        {
            _tvdbClientManager = tvdbClientManager;
            _baseItemManager = baseItemManager;
            _providerManager = providerManager;
            _localization = localization;
            _libraryManager = libraryManager;
            _logger = logger;
        }

        private static bool EpisodeExists(EpisodeBaseRecord episodeRecord, IReadOnlyList<Episode> existingEpisodes)
        {
            return existingEpisodes.Any(episode => EpisodeEquals(episode, episodeRecord));
        }

        private static bool EpisodeEquals(Episode episode, EpisodeBaseRecord otherEpisodeRecord)
        {
            return otherEpisodeRecord.Number.HasValue
                && episode.ContainsEpisodeNumber(otherEpisodeRecord.Number.Value)
                && episode.ParentIndexNumber == otherEpisodeRecord.SeasonNumber;
        }

        /// <summary>
        /// Is Metadata fetcher enabled for Series, Season or Episode.
        /// </summary>
        /// <param name="item">Series, Season or Episode.</param>
        /// <returns>true if enabled.</returns>
        private bool IsEnabledForLibrary(BaseItem item)
        {
            Series? series = item switch
            {
                Episode episode => episode.Series,
                Season season => season.Series,
                _ => item as Series
            };

            if (series == null)
            {
                _logger.LogDebug("Given input is not in {@ValidTypes}: {Type}", new[] { nameof(Series), nameof(Season), nameof(Episode) }, item.GetType());
                return false;
            }

            var libraryOptions = _libraryManager.GetLibraryOptions(series);
            var typeOptions = libraryOptions.GetTypeOptions(series.GetType().Name);
            return _baseItemManager.IsMetadataFetcherEnabled(series, typeOptions, ProviderName);
        }

        // TODO use the new async events when provider manager is updated
        private void OnProviderManagerRefreshComplete(object? sender, GenericEventArgs<BaseItem> genericEventArgs)
        {
            if (!IsEnabledForLibrary(genericEventArgs.Argument))
            {
                _logger.LogDebug("{ProviderName} not enabled for {InputName}", ProviderName, genericEventArgs.Argument.Name);
                return;
            }

            _logger.LogDebug("{MethodName}: Try Refreshing for Item {Name} {Type}", nameof(OnProviderManagerRefreshComplete), genericEventArgs.Argument.Name, genericEventArgs.Argument.GetType());
            if (genericEventArgs.Argument is Series series)
            {
                _logger.LogDebug("{MethodName}: Refreshing Series {SeriesName}", nameof(OnProviderManagerRefreshComplete), series.Name);
                HandleSeries(series).GetAwaiter().GetResult();
            }

            if (genericEventArgs.Argument is Season season)
            {
                _logger.LogDebug("{MethodName}: Refreshing {SeriesName} {SeasonName}", nameof(OnProviderManagerRefreshComplete), season.Series?.Name, season.Name);
                HandleSeason(season).GetAwaiter().GetResult();
            }
        }

        private async Task HandleSeries(Series series)
        {
            if (!series.HasTvdbId())
            {
                _logger.LogDebug("No TVDB Id available.");
                return;
            }

            var tvdbId = series.GetTvdbId();

            var children = series.GetRecursiveChildren();
            var existingSeasons = new List<Season>();
            var existingEpisodes = new Dictionary<int, List<Episode>>();
            for (var i = 0; i < children.Count; i++)
            {
                switch (children[i])
                {
                    case Season season:
                        if (season.IndexNumber.HasValue)
                        {
                            existingSeasons.Add(season);
                        }

                        break;
                    case Episode episode:
                        var seasonNumber = episode.ParentIndexNumber ?? 1;
                        if (!existingEpisodes.TryGetValue(seasonNumber, out var value))
                        {
                            value = new List<Episode>();
                            existingEpisodes[seasonNumber] = value;
                        }

                        value.Add(episode);
                        break;
                }
            }

            var allEpisodes = await GetAllEpisodes(tvdbId, series.GetPreferredMetadataLanguage()).ConfigureAwait(false);
            var allSeasons = allEpisodes
                .Where(ep => ep.SeasonNumber.HasValue)
                .Select(ep => ep.SeasonNumber!.Value)
                .Distinct()
                .ToList();

            // Add missing seasons
            var newSeasons = AddMissingSeasons(series, existingSeasons, allSeasons);
            AddMissingEpisodes(existingEpisodes, allEpisodes, existingSeasons.Concat(newSeasons).ToList());
        }

        private async Task HandleSeason(Season season)
        {
            var series = season.Series;
            if (!series.HasTvdbId())
            {
                _logger.LogDebug("No TVDB Id available.");
                return;
            }

            var tvdbId = series.GetTvdbId();
            var allEpisodes = await GetAllEpisodes(tvdbId, season.GetPreferredMetadataLanguage())
                .ConfigureAwait(false);

            var seasonEpisodes = allEpisodes.Where(e => e.SeasonNumber == season.IndexNumber).ToList();
            var existingEpisodes = season.GetEpisodes().OfType<Episode>().ToHashSet();

            foreach (var episodeRecord in seasonEpisodes)
            {
                var foundEpisodes = existingEpisodes.Where(episode => EpisodeEquals(episode, episodeRecord)).ToList();
                if (foundEpisodes.Count != 0)
                {
                    // So we have at least one existing episode for our episodeRecord
                    var physicalEpisodes = foundEpisodes.Where(e => !e.IsVirtualItem);
                    if (physicalEpisodes.Any())
                    {
                        // if there is a physical episode we can delete existing virtual episode entries
                        var virtualEpisodes = foundEpisodes.Where(e => e.IsVirtualItem).ToList();
                        DeleteVirtualItems(virtualEpisodes);
                        existingEpisodes.ExceptWith(virtualEpisodes);
                    }

                    continue;
                }

                AddVirtualEpisode(episodeRecord, season);
            }

            var orphanedEpisodes = existingEpisodes
                .Where(e => e.IsVirtualItem)
                .Where(e => !seasonEpisodes.Any(episodeRecord => EpisodeEquals(e, episodeRecord)))
                .ToList();
            DeleteVirtualItems(orphanedEpisodes);
        }

        private void OnLibraryManagerItemUpdated(object? sender, ItemChangeEventArgs itemChangeEventArgs)
        {
            _logger.LogDebug("{MethodName}: Refreshing Item {ItemName} [{Reason}]", nameof(OnLibraryManagerItemUpdated), itemChangeEventArgs.Item.Name, itemChangeEventArgs.UpdateReason);
            // Only interested in real Season and Episode items
            if (itemChangeEventArgs.Item.IsVirtualItem
                || !(itemChangeEventArgs.Item is Season || itemChangeEventArgs.Item is Episode))
            {
                _logger.LogDebug("Skip: Updated item is {ItemType}.", itemChangeEventArgs.Item.IsVirtualItem ? "Virtual" : "no Season or Episode");
                return;
            }

            if (!IsEnabledForLibrary(itemChangeEventArgs.Item))
            {
                _logger.LogDebug("{ProviderName} not enabled for {InputName}", ProviderName, itemChangeEventArgs.Item.Name);
                return;
            }

            var existingVirtualItems = GetVirtualItems(itemChangeEventArgs.Item, itemChangeEventArgs.Parent);
            DeleteVirtualItems(existingVirtualItems);
        }

        private List<BaseItem> GetVirtualItems(BaseItem item, BaseItem? parent)
        {
            var query = new InternalItemsQuery
            {
                IsVirtualItem = true,
                IndexNumber = item.IndexNumber,
                // If the item is an Episode, filter on ParentIndexNumber as well (season number)
                ParentIndexNumber = item is Episode ? item.ParentIndexNumber : null,
                IncludeItemTypes = new[] { item.GetBaseItemKind() },
                Parent = parent,
                Recursive = true,
                GroupByPresentationUniqueKey = false,
                DtoOptions = new DtoOptions(true)
            };

            var existingVirtualItems = _libraryManager.GetItemList(query);
            return existingVirtualItems;
        }

        private void DeleteVirtualItems<T>(List<T> existingVirtualItems)
            where T : BaseItem
        {
            var deleteOptions = new DeleteOptions
            {
                DeleteFileLocation = true
            };

            // Remove the virtual season/episode that matches the newly updated item
            for (var i = 0; i < existingVirtualItems.Count; i++)
            {
                var currentItem = existingVirtualItems[i];
                _logger.LogDebug("Delete VirtualItem {Name} - S{Season:00}E{Episode:00}", currentItem.Name, currentItem.ParentIndexNumber, currentItem.IndexNumber);
                _libraryManager.DeleteItem(currentItem, deleteOptions);
            }
        }

        // TODO use async events
        private void OnLibraryManagerItemRemoved(object? sender, ItemChangeEventArgs itemChangeEventArgs)
        {
            _logger.LogDebug("{MethodName}: Refreshing {ItemName} [{Reason}]", nameof(OnLibraryManagerItemRemoved), itemChangeEventArgs.Item.Name, itemChangeEventArgs.UpdateReason);
            // No action needed if the item is virtual
            if (itemChangeEventArgs.Item.IsVirtualItem || !IsEnabledForLibrary(itemChangeEventArgs.Item))
            {
                _logger.LogDebug("Skip: {Message}.", itemChangeEventArgs.Item.IsVirtualItem ? "Updated item is Virtual" : "Update not enabled");
                return;
            }

            // Create a new virtual season if the real one was deleted.
            // Similarly, create a new virtual episode if the real one was deleted.
            if (itemChangeEventArgs.Item is Season season)
            {
                var newSeason = AddVirtualSeason(season.IndexNumber!.Value, season.Series);
                HandleSeason(newSeason).GetAwaiter().GetResult();
            }
            else if (itemChangeEventArgs.Item is Episode episode)
            {
                if (!episode.Series.HasTvdbId())
                {
                    _logger.LogDebug("No TVDB Id available.");
                    return;
                }

                var tvdbId = episode.Series.GetTvdbId();

                var episodeRecords = GetAllEpisodes(tvdbId, episode.GetPreferredMetadataLanguage()).GetAwaiter().GetResult();

                EpisodeBaseRecord? episodeRecord = null;
                if (episodeRecords.Count > 0)
                {
                    episodeRecord = episodeRecords.FirstOrDefault(e => EpisodeEquals(episode, e));
                }

                AddVirtualEpisode(episodeRecord, episode.Season);
            }
        }

        private async Task<IReadOnlyList<EpisodeBaseRecord>> GetAllEpisodes(int tvdbId, string acceptedLanguage)
        {
            try
            {
                // Fetch all episodes for the series
                var seriesInfo = await _tvdbClientManager.GetSeriesEpisodesAsync(tvdbId, acceptedLanguage, "default", CancellationToken.None).ConfigureAwait(false);
                var allEpisodes = seriesInfo.Episodes;
                if (allEpisodes is null || !allEpisodes.Any())
                {
                    _logger.LogWarning("Unable to get episodes from TVDB: Episode Query returned null for TVDB Id: {TvdbId}", tvdbId);
                    return Array.Empty<EpisodeBaseRecord>();
                }

                _logger.LogDebug("{MethodName}: For TVDB Id '{TvdbId}' found #{Count} [{Episodes}]", nameof(GetAllEpisodes), tvdbId, allEpisodes.Count, string.Join(", ", allEpisodes.Select(e => $"S{e.SeasonNumber}E{e.Number}")));
                return allEpisodes;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to get episodes from TVDB for Id '{TvdbId}'", tvdbId);
                return Array.Empty<EpisodeBaseRecord>();
            }
        }

        private IEnumerable<Season> AddMissingSeasons(Series series, List<Season> existingSeasons, IReadOnlyList<int> allSeasons)
        {
            var missingSeasons = allSeasons.Except(existingSeasons.Select(s => s.IndexNumber!.Value)).ToList();
            for (var i = 0; i < missingSeasons.Count; i++)
            {
                var season = missingSeasons[i];
                yield return AddVirtualSeason(season, series);
            }
        }

        private void AddMissingEpisodes(
            Dictionary<int, List<Episode>> existingEpisodes,
            IReadOnlyList<EpisodeBaseRecord> allEpisodeRecords,
            IReadOnlyList<Season> existingSeasons)
        {
            for (var i = 0; i < allEpisodeRecords.Count; i++)
            {
                var episodeRecord = allEpisodeRecords[i];

                // skip if it exists already
                if (episodeRecord.SeasonNumber.HasValue
                    && existingEpisodes.TryGetValue(episodeRecord.SeasonNumber.Value, out var episodes)
                    && EpisodeExists(episodeRecord, episodes))
                {
                    _logger.LogDebug("{MethodName}: Skip, already existing S{Season:00}E{Episode:00}", nameof(AddMissingEpisodes), episodeRecord.SeasonNumber, episodeRecord.Number);
                    continue;
                }

                var existingSeason = existingSeasons.First(season => season.IndexNumber.HasValue && season.IndexNumber.Value == episodeRecord.SeasonNumber);

                AddVirtualEpisode(episodeRecord, existingSeason);
            }
        }

        private Season AddVirtualSeason(int season, Series series)
        {
            string seasonName;
            if (season == 0)
            {
                seasonName = _libraryManager.GetLibraryOptions(series).SeasonZeroDisplayName;
            }
            else
            {
                seasonName = string.Format(
                    CultureInfo.InvariantCulture,
                    _localization.GetLocalizedString("NameSeasonNumber"),
                    season.ToString(CultureInfo.InvariantCulture));
            }

            _logger.LogDebug("Creating Season {SeasonName} entry for {SeriesName}", seasonName, series.Name);

            var newSeason = new Season
            {
                Name = seasonName,
                IndexNumber = season,
                Id = _libraryManager.GetNewItemId(
                    series.Id + season.ToString(CultureInfo.InvariantCulture) + seasonName,
                    typeof(Season)),
                IsVirtualItem = true,
                SeriesId = series.Id,
                SeriesName = series.Name,
                SeriesPresentationUniqueKey = series.GetPresentationUniqueKey()
            };

            series.AddChild(newSeason);

            return newSeason;
        }

        private void AddVirtualEpisode(EpisodeBaseRecord? episode, Season? season)
        {
            if (episode?.SeasonNumber == null || season == null)
            {
                return;
            }

            // Put as much metadata into it as possible
            var newEpisode = new Episode
            {
                Name = episode.Name,
                IndexNumber = episode.Number,
                ParentIndexNumber = episode.SeasonNumber,
                Id = _libraryManager.GetNewItemId(
                    $"{season.Series.Id}{episode.SeasonNumber}Episode {episode.Number}",
                    typeof(Episode)),
                IsVirtualItem = true,
                SeasonId = season.Id,
                SeriesId = season.Series.Id,
                AirsBeforeEpisodeNumber = episode.AirsBeforeEpisode,
                AirsAfterSeasonNumber = episode.AirsAfterSeason,
                AirsBeforeSeasonNumber = episode.AirsBeforeSeason,
                Overview = episode.Overview,
                SeriesName = season.Series.Name,
                SeriesPresentationUniqueKey = season.SeriesPresentationUniqueKey,
                SeasonName = season.Name,
                DateLastSaved = DateTime.UtcNow
            };
            if (DateTime.TryParse(episode!.Aired, out var premiereDate))
            {
                newEpisode.PremiereDate = premiereDate;
            }

            newEpisode.PresentationUniqueKey = newEpisode.GetPresentationUniqueKey();
            newEpisode.SetTvdbId(episode.Id);

            _logger.LogDebug(
                "Creating virtual episode {SeriesName} S{Season:00}E{Episode:00}",
                season.Series.Name,
                episode.SeasonNumber,
                episode.Number);

            season.AddChild(newEpisode);
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _providerManager.RefreshCompleted += OnProviderManagerRefreshComplete;
            _libraryManager.ItemUpdated += OnLibraryManagerItemUpdated;
            _libraryManager.ItemRemoved += OnLibraryManagerItemRemoved;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _providerManager.RefreshCompleted -= OnProviderManagerRefreshComplete;
            _libraryManager.ItemUpdated -= OnLibraryManagerItemUpdated;
            _libraryManager.ItemRemoved -= OnLibraryManagerItemRemoved;
            return Task.CompletedTask;
        }
    }
}
