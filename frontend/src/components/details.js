var Details = {
    container: null,
    currentItem: null,
    isVisible: false,

    init: function() {
        console.log('[Moonfin] Details: Initializing...');
        this.createContainer();
        this.setupItemInterception();
        console.log('[Moonfin] Details: Initialized');
    },

    createContainer: function() {
        var existing = document.querySelector('.moonfin-details-overlay');
        if (existing) existing.remove();

        this.container = document.createElement('div');
        this.container.className = 'moonfin-details-overlay';
        this.container.innerHTML = '<div class="moonfin-details-panel"></div>';
        document.body.appendChild(this.container);
    },

    setupItemInterception: function() {
        var self = this;

        if (!Storage.get('detailsPageEnabled')) {
            console.log('[Moonfin] Details: Custom details page disabled by setting');
            return;
        }
        
        var ignoreSelectors = '.videoOsdBottom, .videoOsdTop, .osdHeader, .videoOsd, .subtitleAppearanceDialog, .subtitleSync, .trackSelections, .playerStats, .dialog, .dialogContainer, .focuscontainer-down, .actionSheetContent, .actionSheet, .actionSheetScroller, .videoPlayerContainer, .upNextContainer, .mediaSelectionMenu, .slideshowButtonContainer, .btnVideoOsd, .osdMediaInfo, .osdControls, .skipSegmentContainer, .itemContextMenu, .popupContainer, .toast, .guide, .recordingFields, .formDialogContent, .formDialog, .promptDialog, .confirmDialog, .withPopup, .multiSelectMenu, .moonfin-more-menu, .moonfin-settings-panel';

        document.addEventListener('click', function(e) {
            if (e.target.closest(ignoreSelectors)) {
                return;
            }

            var card = e.target.closest('.card, .listItem');
            if (!card) return;

            if (e.target.closest('.cardOverlayButton, .listItemButton, .btnPlayItem, .btnMoreCommands, .btnUserItemRating, .btnItemAction, .paper-icon-button-light, .itemAction[data-action]:not([data-action="link"])')) {
                return;
            }

            if (!card.closest('.homeSection, .section, .itemsContainer, .cardContainer, .listTopPager, .vertical-list, .vertical-wrap, .prefContainer, .libraryPage, .pageTabContent, .sectionTitleContainer, .moonfin-details-panel, .moonfin-mediabar')) {
                return;
            }

            var itemId = self.getItemIdFromCard(card);
            if (!itemId) return;

            var cardType = card.getAttribute('data-type') || 
                          (card.querySelector('[data-type]') ? card.querySelector('[data-type]').getAttribute('data-type') : null) ||
                          self.inferCardType(card);
            
            if (['Movie', 'Series', 'Episode', 'Season'].indexOf(cardType) !== -1) {
                e.preventDefault();
                e.stopPropagation();
                e.stopImmediatePropagation();
                self.showDetails(itemId, cardType);
                return false;
            }
            if (!cardType && card.classList.contains('card')) {
                e.preventDefault();
                e.stopPropagation();
                e.stopImmediatePropagation();
                self.showDetails(itemId, null);
                return false;
            }
        }, true);

        document.addEventListener('click', function(e) {
            if (e.target.closest(ignoreSelectors)) {
                return;
            }

            var link = e.target.closest('a[href*="id="], a[href*="/details"]');
            if (!link) return;
            
            var card = link.closest('.card, .listItem');
            if (!card) return;
            
            var itemId = self.getItemIdFromCard(card) || self.getItemIdFromLink(link);
            if (!itemId) return;
            
            var cardType = card.getAttribute('data-type') || self.inferCardType(card);
            
            if (!cardType || ['Movie', 'Series', 'Episode', 'Season'].indexOf(cardType) !== -1) {
                e.preventDefault();
                e.stopPropagation();
                e.stopImmediatePropagation();
                self.showDetails(itemId, cardType);
                return false;
            }
        }, true);

        document.addEventListener('keydown', function(e) {
            if (e.key === 'Enter' || e.keyCode === 13) {
                if (e.target.closest(ignoreSelectors)) {
                    return;
                }

                var focused = document.activeElement;
                var card = (focused ? focused.closest('.card, .listItem') : null) || 
                          (focused && focused.classList.contains('card') ? focused : null);
                
                if (card) {
                    var itemId = self.getItemIdFromCard(card);
                    var cardType = card.getAttribute('data-type') || self.inferCardType(card);
                    
                    if (itemId && (!cardType || ['Movie', 'Series', 'Episode', 'Season'].indexOf(cardType) !== -1)) {
                        e.preventDefault();
                        e.stopPropagation();
                        e.stopImmediatePropagation();
                        self.showDetails(itemId, cardType);
                        return false;
                    }
                }
            }
        }, true);

        // Close on back button — keyCodes 461 (LG) and 10009 (Samsung) are TV remote back buttons
        document.addEventListener('keydown', function(e) {
            if (self.isVisible && (e.key === 'Escape' || e.keyCode === 27 || e.keyCode === 461 || e.keyCode === 10009)) {
                e.preventDefault();
                e.stopPropagation();
                self.hide();
            }
        }, true);
    },

    getItemIdFromCard: function(card) {
        var idFromAttr = card.getAttribute('data-id') || card.getAttribute('data-itemid');
        if (idFromAttr) return idFromAttr;
        
        var dataIdEl = card.querySelector('[data-id]');
        if (dataIdEl) return dataIdEl.getAttribute('data-id');
        
        var link = card.querySelector('a');
        if (link && link.href) {
            var match = link.href.match(/id=([a-f0-9]+)/i) || link.href.match(/\/([a-f0-9]{32})/i);
            if (match) return match[1];
        }
        return null;
    },

    getItemIdFromLink: function(link) {
        if (!link || !link.href) return null;
        var match = link.href.match(/id=([a-f0-9]+)/i) || 
                   link.href.match(/\/details\?id=([a-f0-9]+)/i) ||
                   link.href.match(/\/([a-f0-9]{32})/i);
        return match ? match[1] : null;
    },

    inferCardType: function(card) {
        var classList = card.className.toLowerCase();
        if (classList.indexOf('movie') !== -1) return 'Movie';
        if (classList.indexOf('series') !== -1) return 'Series';
        if (classList.indexOf('episode') !== -1) return 'Episode';
        if (classList.indexOf('season') !== -1) return 'Season';
        
        var section = card.closest('.homeSection, .section');
        if (section) {
            var sectionTitle = section.querySelector('.sectionTitle');
            var title = sectionTitle ? sectionTitle.textContent.toLowerCase() : '';
            if (title.indexOf('movie') !== -1) return 'Movie';
            if (title.indexOf('series') !== -1 || title.indexOf('show') !== -1) return 'Series';
            if (title.indexOf('episode') !== -1) return 'Episode';
        }
        
        return null;
    },

    showDetails: function(itemId, itemType) {
        var self = this;
        console.log('[Moonfin] Details: Loading item', itemId, itemType);

        var api = API.getApiClient();
        if (!api) {
            console.error('[Moonfin] Details: No API client');
            return;
        }

        // Only push history state when first opening (not when navigating within overlay)
        var wasAlreadyVisible = this.isVisible;

        this.container.classList.add('visible');
        this.isVisible = true;
        document.body.classList.add('moonfin-details-visible');

        if (!wasAlreadyVisible) {
            history.pushState({ moonfinDetails: true }, '');
        }

        var panel = this.container.querySelector('.moonfin-details-panel');
        panel.innerHTML = '<div class="moonfin-details-loading"><div class="moonfin-spinner"></div><span>Loading...</span></div>';

        this.fetchItem(api, itemId).then(function(item) {
            self.currentItem = item;

            var supportedTypes = ['Movie', 'Series', 'Episode', 'Season', 'Person', 'BoxSet'];
            if (supportedTypes.indexOf(item.Type) === -1) {
                self.hide(true);
                API.navigateToItem(itemId);
                return;
            }

            if (item.Type === 'Person') {
                var personItemsPromise = self.fetchPersonItems(api, itemId).catch(function() { return []; });
                return personItemsPromise.then(function(personItems) {
                    self.renderPersonDetails(item, personItems);

                    setTimeout(function() {
                        var firstBtn = panel.querySelector('.moonfin-btn, .moonfin-btn-wrapper, .moonfin-focusable');
                        if (firstBtn) firstBtn.focus();
                    }, 100);
                });
            }

            var similarPromise = self.fetchSimilar(api, itemId).catch(function() { return []; });
            var castPromise = Promise.resolve(item.People || []);
            var seasonsPromise = item.Type === 'Series' ? self.fetchSeasons(api, itemId).catch(function() { return []; }) : Promise.resolve([]);
            var episodesPromise = (item.Type === 'Episode' && item.SeasonId) ? self.fetchEpisodes(api, item.SeriesId, item.SeasonId).catch(function() { return []; }) : ((item.Type === 'Season' && item.SeriesId) ? self.fetchEpisodes(api, item.SeriesId, item.Id).catch(function() { return []; }) : Promise.resolve([]));

            return Promise.all([similarPromise, castPromise, seasonsPromise, episodesPromise]).then(function(results) {
                var similar = results[0];
                var cast = results[1];
                var seasons = results[2];
                var episodes = results[3];
                
                if (item.Type === 'Season') {
                    self.renderSeasonDetails(item, episodes);
                } else {
                    self.renderDetails(item, similar, cast, seasons, episodes);
                }

                if (MdbList.isEnabled()) {
                    MdbList.fetchRatings(item).then(function(ratings) {
                        if (ratings && ratings.length > 0 && self.currentItem && self.currentItem.Id === item.Id) {
                            self.renderMdbListRatings(ratings);
                        }
                    });
                }

                if (Tmdb.isEnabled() && item.Type === 'Episode') {
                    Tmdb.fetchRatingForEpisode(item).then(function(rating) {
                        if (rating && self.currentItem && self.currentItem.Id === item.Id) {
                            self.renderTmdbEpisodeRating(rating);
                        }
                    });
                    if (item.SeriesId && episodes.length > 0) {
                        self.fetchTmdbRatingsForEpisodeList(item, episodes);
                    }
                }

                setTimeout(function() {
                    var firstBtn = panel.querySelector('.moonfin-btn');
                    if (firstBtn) firstBtn.focus();
                }, 100);
            });
        }).catch(function(err) {
            console.error('[Moonfin] Details: Error loading item', err);
            panel.innerHTML = '<div class="moonfin-details-error"><span>Failed to load details</span><button class="moonfin-btn moonfin-focusable" onclick="Details.hide()">Close</button></div>';
        });
    },

    fetchItem: function(api, itemId) {
        var userId = api.getCurrentUserId();
        return api.getItem(userId, itemId);
    },

    fetchSimilar: function(api, itemId) {
        var userId = api.getCurrentUserId();
        return api.getSimilarItems(itemId, {
            userId: userId,
            limit: 12,
            fields: 'PrimaryImageAspectRatio,UserData'
        }).then(function(result) {
            return result.Items || [];
        });
    },

    fetchSeasons: function(api, seriesId) {
        var userId = api.getCurrentUserId();
        return api.getSeasons(seriesId, {
            userId: userId,
            fields: 'PrimaryImageAspectRatio,UserData'
        }).then(function(result) {
            return result.Items || [];
        });
    },

    fetchEpisodes: function(api, seriesId, seasonId) {
        var userId = api.getCurrentUserId();
        var serverUrl = api._serverAddress || api.serverAddress();
        var headers = this.getAuthHeaders();

        return fetch(serverUrl + '/Shows/' + seriesId + '/Episodes?UserId=' + userId + '&SeasonId=' + seasonId + '&Fields=Overview,PrimaryImageAspectRatio', {
            headers: headers
        }).then(function(resp) {
            return resp.json();
        }).then(function(result) {
            return result.Items || [];
        });
    },

    fetchPersonItems: function(api, personId) {
        var userId = api.getCurrentUserId();
        var serverUrl = api._serverAddress || api.serverAddress();
        var headers = this.getAuthHeaders();

        return fetch(serverUrl + '/Users/' + userId + '/Items?PersonIds=' + personId + '&Recursive=true&IncludeItemTypes=Movie,Series&SortBy=PremiereDate,SortName&SortOrder=Descending&Fields=PrimaryImageAspectRatio,Overview&Limit=50', {
            headers: headers
        }).then(function(resp) {
            return resp.json();
        }).then(function(result) {
            return result.Items || [];
        });
    },

    renderDetails: function(item, similar, cast, seasons, episodes) {
        var self = this;
        var panel = this.container.querySelector('.moonfin-details-panel');
        var api = API.getApiClient();
        var serverUrl = api._serverAddress;

        var backdropId = (item.BackdropImageTags && item.BackdropImageTags.length > 0) ? item.Id : 
                        (item.ParentBackdropItemId || item.Id);
        var backdropUrl = serverUrl + '/Items/' + backdropId + '/Images/Backdrop?maxWidth=1920&quality=90';
        
        var posterId = item.Id;
        var posterTag = item.ImageTags ? item.ImageTags.Primary : null;
        var isEpisodeThumb = (item.Type === 'Episode');
        var thumbTag = item.ImageTags ? item.ImageTags.Thumb : null;
        var posterUrl;
        if (isEpisodeThumb && thumbTag) {
            posterUrl = serverUrl + '/Items/' + posterId + '/Images/Thumb?maxWidth=500&quality=90';
        } else if (isEpisodeThumb && posterTag) {
            // Episode without Thumb — use Primary but it'll display in landscape container
            posterUrl = serverUrl + '/Items/' + posterId + '/Images/Primary?maxWidth=500&quality=90';
        } else {
            posterUrl = posterTag ? serverUrl + '/Items/' + posterId + '/Images/Primary?maxHeight=500&quality=90' : '';
        }
        
        var logoTag = item.ImageTags ? item.ImageTags.Logo : null;
        var logoUrl = logoTag ? serverUrl + '/Items/' + item.Id + '/Images/Logo?maxWidth=400&quality=90' : null;

        var runtime = item.RunTimeTicks ? this.formatRuntime(item.RunTimeTicks) : '';
        
        var year = item.ProductionYear || (item.PremiereDate ? new Date(item.PremiereDate).getFullYear() : '');
        
        var rating = item.OfficialRating || '';
        
        var communityRating = item.CommunityRating ? item.CommunityRating.toFixed(1) : '';
        
        var criticRating = item.CriticRating;
        
        var genres = (item.Genres || []).join(', ');
        
        var directors = (item.People || []).filter(function(p) { return p.Type === 'Director'; })
            .map(function(p) { return p.Name; }).join(', ');
        
        var writers = (item.People || []).filter(function(p) { return p.Type === 'Writer'; })
            .map(function(p) { return p.Name; }).join(', ');
        
        var studios = (item.Studios || []).map(function(s) { return s.Name; }).join(', ');
        
        var tagline = (item.Taglines && item.Taglines.length > 0) ? item.Taglines[0] : '';
        
        var badges = this.getMediaBadges(item);
        
        var isFavorite = item.UserData ? item.UserData.IsFavorite : false;
        var isPlayed = item.UserData ? item.UserData.Played : false;
        var resumePosition = item.UserData ? (item.UserData.PlaybackPositionTicks || 0) : 0;
        var hasResume = resumePosition > 0;
        var isEpisode = item.Type === 'Episode';
        var isSeries = item.Type === 'Series';
        var seasonCount = item.ChildCount || seasons.length || 0;

        var infoItems = [];
        if (year) infoItems.push('<span class="moonfin-info-item">' + year + '</span>');
        if (rating) infoItems.push('<span class="moonfin-info-pill">' + rating + '</span>');
        if (communityRating) infoItems.push('<span class="moonfin-info-item moonfin-star-rating"><svg viewBox="0 -960 960 960" fill="currentColor" width="16" height="16"><path d="m354-287 126-76 126 77-33-144 111-96-146-13-58-136-58 135-146 13 111 97-33 143ZM233-120l65-281L80-590l288-25 112-265 112 265 288 25-218 189 65 281-247-149-247 149Z"/></svg> ' + communityRating + '</span>');
        if (criticRating) infoItems.push('<span class="moonfin-info-item moonfin-critic-rating">' + criticRating + '%</span>');
        if (runtime && item.Type !== 'Series') infoItems.push('<span class="moonfin-info-item">' + runtime + '</span>');
        if (isSeries && seasonCount > 0) {
            infoItems.push('<span class="moonfin-info-item">' + seasonCount + ' Season' + (seasonCount !== 1 ? 's' : '') + '</span>');
        }
        badges.forEach(function(badge) { infoItems.push(badge); });
        var infoRowHtml = infoItems.length > 0 ? '<div class="moonfin-info-row">' + infoItems.join('') + '</div>' : '';

        var episodeHeader = '';
        if (isEpisode) {
            var epInfo = '';
            if (item.ParentIndexNumber !== undefined && item.IndexNumber !== undefined) {
                epInfo = 'S' + item.ParentIndexNumber + ' E' + item.IndexNumber;
            }
            episodeHeader = '<div class="moonfin-episode-header">' +
                (item.SeriesName ? '<span class="moonfin-series-name">' + item.SeriesName + '</span>' : '') +
                (epInfo ? '<span class="moonfin-episode-number">' + epInfo + '</span>' : '') +
            '</div>';
        }

        var actionBtns = [];
        
        if (hasResume) {
            actionBtns.push(
                '<div class="moonfin-btn-wrapper moonfin-focusable" data-action="play" tabindex="0">' +
                    '<div class="moonfin-btn-circle moonfin-btn-primary">' +
                        '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M8 5v14l11-7z"/></svg>' +
                    '</div>' +
                    '<span class="moonfin-btn-label">Resume</span>' +
                '</div>'
            );
        }
        
        actionBtns.push(
            '<div class="moonfin-btn-wrapper moonfin-focusable" data-action="' + (hasResume ? 'restart' : 'play') + '" tabindex="0">' +
                '<div class="moonfin-btn-circle">' +
                    (hasResume ?
                        '<svg viewBox="0 -960 960 960" fill="currentColor"><path d="M480-80q-75 0-140.5-28.5t-114-77q-48.5-48.5-77-114T120-440h80q0 117 81.5 198.5T480-160q117 0 198.5-81.5T760-440q0-117-81.5-198.5T480-720h-6l62 62-56 58-160-160 160-160 56 58-62 62h6q75 0 140.5 28.5t114 77q48.5 48.5 77 114T840-440q0 75-28.5 140.5t-77 114q-48.5 48.5-114 77T480-80Z"/></svg>' :
                        '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M8 5v14l11-7z"/></svg>') +
                '</div>' +
                '<span class="moonfin-btn-label">' + (hasResume ? 'Restart' : 'Play') + '</span>' +
            '</div>'
        );
        
        var hasTrailer = (item.RemoteTrailers && item.RemoteTrailers.length > 0) || (item.LocalTrailerCount > 0);
        if (hasTrailer) {
            actionBtns.push(
                '<div class="moonfin-btn-wrapper moonfin-focusable" data-action="trailer" tabindex="0">' +
                    '<div class="moonfin-btn-circle">' +
                        '<svg viewBox="0 -960 960 960" fill="currentColor"><path d="M160-120v-720h80v80h80v-80h320v80h80v-80h80v720h-80v-80h-80v80H320v-80h-80v80h-80Zm80-160h80v-80h-80v80Zm0-160h80v-80h-80v80Zm0-160h80v-80h-80v80Zm400 320h80v-80h-80v80Zm0-160h80v-80h-80v80Zm0-160h80v-80h-80v80ZM400-200h160v-560H400v560Zm0-560h160-160Z"/></svg>' +
                    '</div>' +
                    '<span class="moonfin-btn-label">Trailer</span>' +
                '</div>'
            );
        }

        if (isSeries) {
            actionBtns.push(
                '<div class="moonfin-btn-wrapper moonfin-focusable" data-action="shuffle" tabindex="0">' +
                    '<div class="moonfin-btn-circle">' +
                        '<svg viewBox="0 -960 960 960" fill="currentColor"><path d="M560-160v-80h104L537-367l57-57 126 126v-102h80v240H560Zm-344 0-56-56 504-504H560v-80h240v240h-80v-104L216-160Zm151-377L160-744l56-56 207 207-56 56Z"/></svg>' +
                    '</div>' +
                    '<span class="moonfin-btn-label">Shuffle</span>' +
                '</div>'
            );
        }
        
        actionBtns.push(
            '<div class="moonfin-btn-wrapper moonfin-focusable ' + (isPlayed ? 'active' : '') + '" data-action="played" tabindex="0">' +
                '<div class="moonfin-btn-circle">' +
                    '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M21 7L9 19l-5.5-5.5 1.41-1.41L9 16.17 19.59 5.59 21 7z"/></svg>' +
                '</div>' +
                '<span class="moonfin-btn-label">' + (isPlayed ? 'Watched' : 'Unwatched') + '</span>' +
            '</div>'
        );
        
        actionBtns.push(
            '<div class="moonfin-btn-wrapper moonfin-focusable ' + (isFavorite ? 'active' : '') + '" data-action="favorite" tabindex="0">' +
                '<div class="moonfin-btn-circle">' +
                    '<svg viewBox="0 -960 960 960" fill="currentColor"><path d="' + (isFavorite ? 
                        'm480-120-58-52q-101-91-167-157T150-447.5Q111-500 95.5-544T80-634q0-94 63-157t157-63q52 0 99 22t81 62q34-40 81-62t99-22q94 0 157 63t63 157q0 46-15.5 90T810-447.5Q771-395 705-329T538-172l-58 52Z' :
                        'M480-120q-14 0-28.5-5T426-140q-43-38-97.5-82.5T232-308q-41.5-41.5-72-83T122-475q-8-32-11-60.5T108-596q0-86 57-147t147-61q52 0 99 22t69 62q22-40 69-62t99-22q90 0 147 61t57 147q0 32-3 60.5T837-475q-7 42-37.5 83.5T728-308q-42 42-96.5 86.5T534-140q-11 10-25.5 15t-28.5 5Zm0-80q41-37 88.5-75t83-68.5q35.5-30.5 61-58T746-456q9-27 11.5-49t2.5-43q0-53-34.5-91.5T636-678q-43 0-77.5 24T507-602h-54q-17-28-51.5-52T324-678q-55 0-89.5 38.5T200-548q0 21 2.5 43t11.5 49q9 27 34.5 54.5t61 58Q345-313 392.5-275T480-200Z') +
                    '"/></svg>' +
                '</div>' +
                '<span class="moonfin-btn-label">' + (isFavorite ? 'Favorited' : 'Favorite') + '</span>' +
            '</div>'
        );
        
        var audioTracks = (item.MediaStreams || []).filter(function(s) { return s.Type === 'Audio'; });
        if (audioTracks.length > 1) {
            var defaultAudio = item.MediaSources && item.MediaSources[0] ? item.MediaSources[0].DefaultAudioStreamIndex : null;
            var selectedAudioTrack = null;
            for (var ai = 0; ai < audioTracks.length; ai++) {
                if (audioTracks[ai].Index === defaultAudio) { selectedAudioTrack = audioTracks[ai]; break; }
            }
            var audioLabel = selectedAudioTrack ? (selectedAudioTrack.DisplayTitle || 'Audio') : 'Audio';
            actionBtns.push(
                '<div class="moonfin-btn-wrapper moonfin-focusable" data-action="audio" tabindex="0">' +
                    '<div class="moonfin-btn-circle">' +
                        '<svg viewBox="0 -960 960 960" fill="currentColor"><path d="M400-120q-66 0-113-47t-47-113q0-66 47-113t113-47q23 0 42.5 5.5T480-418v-422h240v160H560v400q0 66-47 113t-113 47Z"/></svg>' +
                    '</div>' +
                    '<span class="moonfin-btn-label">' + audioLabel + '</span>' +
                '</div>'
            );
            this._selectedAudioIndex = defaultAudio;
        } else {
            this._selectedAudioIndex = null;
        }

        var subtitleTracks = (item.MediaStreams || []).filter(function(s) { return s.Type === 'Subtitle'; });
        if (subtitleTracks.length > 0) {
            var defaultSub = item.MediaSources && item.MediaSources[0] ? item.MediaSources[0].DefaultSubtitleStreamIndex : -1;
            if (defaultSub == null) defaultSub = -1;
            var selectedSubTrack = null;
            for (var si = 0; si < subtitleTracks.length; si++) {
                if (subtitleTracks[si].Index === defaultSub) { selectedSubTrack = subtitleTracks[si]; break; }
            }
            var subLabel = defaultSub === -1 ? 'Off' : (selectedSubTrack ? (selectedSubTrack.DisplayTitle || 'Subtitles') : 'Subtitles');
            actionBtns.push(
                '<div class="moonfin-btn-wrapper moonfin-focusable" data-action="subtitle" tabindex="0">' +
                    '<div class="moonfin-btn-circle">' +
                        '<svg viewBox="0 -960 960 960" fill="currentColor"><path d="M200-160q-33 0-56.5-23.5T120-240v-480q0-33 23.5-56.5T200-800h560q33 0 56.5 23.5T840-720v480q0 33-23.5 56.5T760-160H200Zm0-80h560v-480H200v480Zm80-120h120q17 0 28.5-11.5T440-400v-40h-60v20h-80v-120h80v20h60v-40q0-17-11.5-28.5T400-600H280q-17 0-28.5 11.5T240-560v160q0 17 11.5 28.5T280-360Zm280 0h120q17 0 28.5-11.5T720-400v-40h-60v20h-80v-120h80v20h60v-40q0-17-11.5-28.5T680-600H560q-17 0-28.5 11.5T520-560v160q0 17 11.5 28.5T560-360ZM200-240v-480 480Z"/></svg>' +
                    '</div>' +
                    '<span class="moonfin-btn-label">' + subLabel + '</span>' +
                '</div>'
            );
            this._selectedSubtitleIndex = defaultSub;
        } else {
            this._selectedSubtitleIndex = -1;
        }

        if (isEpisode && item.SeriesId) {
            actionBtns.push(
                '<div class="moonfin-btn-wrapper moonfin-focusable" data-action="series" tabindex="0">' +
                    '<div class="moonfin-btn-circle">' +
                        '<svg viewBox="0 -960 960 960" fill="currentColor"><path d="M320-120v-80l40-40H160q-33 0-56.5-23.5T80-320v-440q0-33 23.5-56.5T160-840h640q33 0 56.5 23.5T880-760v440q0 33-23.5 56.5T800-240H680l40 40v80H320Z"/></svg>' +
                    '</div>' +
                    '<span class="moonfin-btn-label">Go to Series</span>' +
                '</div>'
            );
        }
        
        actionBtns.push(
            '<div class="moonfin-btn-wrapper moonfin-focusable" data-action="more" tabindex="0">' +
                '<div class="moonfin-btn-circle">' +
                    '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M12 8c1.1 0 2-.9 2-2s-.9-2-2-2-2 .9-2 2 .9 2 2 2zm0 2c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2zm0 6c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2z"/></svg>' +
                '</div>' +
                '<span class="moonfin-btn-label">More</span>' +
            '</div>'
        );

        var metadataRows = [];
        if (genres) metadataRows.push('<div class="moonfin-metadata-cell"><span class="moonfin-metadata-label">Genres</span><span class="moonfin-metadata-value">' + genres + '</span></div>');
        if (directors) metadataRows.push('<div class="moonfin-metadata-cell"><span class="moonfin-metadata-label">Director</span><span class="moonfin-metadata-value">' + directors + '</span></div>');
        if (writers) metadataRows.push('<div class="moonfin-metadata-cell"><span class="moonfin-metadata-label">Writers</span><span class="moonfin-metadata-value">' + writers + '</span></div>');
        if (studios) metadataRows.push('<div class="moonfin-metadata-cell"><span class="moonfin-metadata-label">Studio</span><span class="moonfin-metadata-value">' + studios + '</span></div>');
        if (runtime) metadataRows.push('<div class="moonfin-metadata-cell"><span class="moonfin-metadata-label">Runtime</span><span class="moonfin-metadata-value">' + runtime + '</span></div>');
        if (isSeries && seasonCount > 0) metadataRows.push('<div class="moonfin-metadata-cell"><span class="moonfin-metadata-label">Seasons</span><span class="moonfin-metadata-value">' + seasonCount + '</span></div>');
        
        var metadataHtml = metadataRows.length > 0 ? '<div class="moonfin-metadata-group">' + metadataRows.join('') + '</div>' : '';

        var arrowsHtml = '<div class="moonfin-section-arrows">' +
            '<button class="moonfin-section-arrow moonfin-arrow-left" aria-label="Scroll left">' +
                '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M15.41 7.41L14 6l-6 6 6 6 1.41-1.41L10.83 12z"/></svg>' +
            '</button>' +
            '<button class="moonfin-section-arrow moonfin-arrow-right" aria-label="Scroll right">' +
                '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M10 6L8.59 7.41 13.17 12l-4.58 4.59L10 18l6-6z"/></svg>' +
            '</button>' +
        '</div>';

        var castHtml = cast.slice(0, 15).map(function(person) {
            var personImg = person.PrimaryImageTag ? 
                serverUrl + '/Items/' + person.Id + '/Images/Primary?maxHeight=280&quality=80' : '';
            return '<div class="moonfin-cast-card moonfin-focusable" data-person-id="' + person.Id + '" tabindex="0">' +
                '<div class="moonfin-cast-photo">' +
                    (personImg ? '<img src="' + personImg + '" alt="" loading="lazy">' : '<svg viewBox="0 0 24 24"><path fill="currentColor" d="M12 4a4 4 0 0 1 4 4 4 4 0 0 1-4 4 4 4 0 0 1-4-4 4 4 0 0 1 4-4m0 10c4.42 0 8 1.79 8 4v2H4v-2c0-2.21 3.58-4 8-4"/></svg>') +
                '</div>' +
                '<span class="moonfin-cast-name">' + person.Name + '</span>' +
                '<span class="moonfin-cast-role">' + (person.Role || person.Type || '') + '</span>' +
            '</div>';
        }).join('');

        var similarHtml = similar.slice(0, 12).map(function(sim) {
            var simPosterTag = sim.ImageTags ? sim.ImageTags.Primary : null;
            var simPosterUrl = simPosterTag ? serverUrl + '/Items/' + sim.Id + '/Images/Primary?maxHeight=400&quality=80' : '';
            var simWatched = sim.UserData && sim.UserData.Played;
            return '<div class="moonfin-similar-card moonfin-focusable" data-item-id="' + sim.Id + '" data-type="' + sim.Type + '" tabindex="0">' +
                '<div class="moonfin-similar-poster">' +
                    (simPosterUrl ? '<img src="' + simPosterUrl + '" alt="" loading="lazy">' : '') +
                    (simWatched ? '<div class="moonfin-watched-indicator"><svg viewBox="0 0 24 24" fill="currentColor"><path d="M21 7L9 19l-5.5-5.5 1.41-1.41L9 16.17 19.59 5.59 21 7z"/></svg></div>' : '') +
                '</div>' +
                '<span class="moonfin-similar-title">' + sim.Name + '</span>' +
            '</div>';
        }).join('');

        var seasonsHtml = seasons.length > 0 ? (
            '<div class="moonfin-section">' +
                '<div class="moonfin-section-header">' +
                    '<h3 class="moonfin-section-title">Seasons</h3>' +
                    arrowsHtml +
                '</div>' +
                '<div class="moonfin-section-scroll">' +
                    seasons.map(function(season) {
                        var seasonPosterTag = season.ImageTags ? season.ImageTags.Primary : null;
                        var seasonPoster = seasonPosterTag ? serverUrl + '/Items/' + season.Id + '/Images/Primary?maxHeight=350&quality=80' : '';
                        var seasonWatched = season.UserData && season.UserData.Played;
                        var seasonUnplayed = season.UserData ? season.UserData.UnplayedItemCount : null;
                        return '<div class="moonfin-season-card moonfin-focusable" data-item-id="' + season.Id + '" data-type="Season" tabindex="0">' +
                            '<div class="moonfin-season-poster">' +
                                (seasonPoster ? '<img src="' + seasonPoster + '" alt="" loading="lazy">' : '<span>' + season.Name + '</span>') +
                                (seasonWatched ? '<div class="moonfin-watched-indicator"><svg viewBox="0 0 24 24" fill="currentColor"><path d="M21 7L9 19l-5.5-5.5 1.41-1.41L9 16.17 19.59 5.59 21 7z"/></svg></div>' :
                                (seasonUnplayed > 0 ? '<div class="moonfin-unplayed-count">' + seasonUnplayed + '</div>' : '')) +
                            '</div>' +
                            '<span class="moonfin-season-name">' + season.Name + '</span>' +
                        '</div>';
                    }).join('') +
                '</div>' +
            '</div>'
        ) : '';

        var episodesArr = episodes || [];
        var episodesHtml = '';
        if (isEpisode && episodesArr.length > 0) {
            var seasonLabel = item.ParentIndexNumber !== undefined ? 'Season ' + item.ParentIndexNumber + ' Episodes' : 'Episodes';
            var epCards = episodesArr.map(function(ep) {
                var epThumbTag = ep.ImageTags ? ep.ImageTags.Primary : null;
                var epThumbUrl = epThumbTag ? serverUrl + '/Items/' + ep.Id + '/Images/Primary?maxWidth=400&quality=80' : '';
                var isCurrentEp = ep.Id === item.Id;
                var epRuntime = ep.RunTimeTicks ? self.formatRuntime(ep.RunTimeTicks) : '';
                return '<div class="moonfin-episode-card moonfin-focusable' + (isCurrentEp ? ' moonfin-episode-current' : '') + '" data-item-id="' + ep.Id + '" data-type="Episode" tabindex="0">' +
                    '<div class="moonfin-episode-thumb">' +
                        (epThumbUrl ? '<img src="' + epThumbUrl + '" alt="" loading="lazy">' : '<div class="moonfin-episode-thumb-placeholder"><svg viewBox="0 0 24 24" fill="currentColor"><path d="M21 3H3c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h18c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm0 16H3V5h18v14zM9.5 7.5l7 4.5-7 4.5z"/></svg></div>') +
                        (ep.UserData && ep.UserData.PlayedPercentage ? '<div class="moonfin-episode-progress"><div class="moonfin-episode-progress-bar" style="width:' + Math.min(ep.UserData.PlayedPercentage, 100) + '%"></div></div>' : '') +
                    '</div>' +
                    '<div class="moonfin-episode-info">' +
                        '<span class="moonfin-episode-ep-number">E' + (ep.IndexNumber || '?') + '</span>' +
                        '<span class="moonfin-episode-ep-title">' + ep.Name + '</span>' +
                        (epRuntime ? '<span class="moonfin-episode-ep-runtime">' + epRuntime + '</span>' : '') +
                    '</div>' +
                '</div>';
            }).join('');

            episodesHtml = '<div class="moonfin-section">' +
                '<div class="moonfin-section-header">' +
                    '<h3 class="moonfin-section-title">' + seasonLabel + '</h3>' +
                    arrowsHtml +
                '</div>' +
                '<div class="moonfin-section-scroll">' + epCards + '</div>' +
            '</div>';
        }

        panel.innerHTML = 
            '<div class="moonfin-details-backdrop" style="background-image: url(\'' + backdropUrl + '\')"></div>' +
            '<div class="moonfin-details-gradient"></div>' +
            
            '<button class="moonfin-details-back moonfin-focusable" title="Back" tabindex="0">' +
                '<svg viewBox="0 0 24 24"><path fill="currentColor" d="M20 11H7.83l5.59-5.59L12 4l-8 8 8 8 1.41-1.41L7.83 13H20v-2z"/></svg>' +
            '</button>' +
            
            '<div class="moonfin-details-content">' +
                '<div class="moonfin-details-header">' +
                    '<div class="moonfin-info-section">' +
                        episodeHeader +
                        '<div class="moonfin-title-section">' +
                            (logoUrl ? '<img class="moonfin-logo" src="' + logoUrl + '" alt="' + item.Name + '">' : '<h1 class="moonfin-title">' + item.Name + '</h1>') +
                        '</div>' +
                        infoRowHtml +
                        '<div class="moonfin-mdblist-ratings-row" id="moonfin-details-mdblist"></div>' +
                        (tagline ? '<p class="moonfin-tagline">&ldquo;' + tagline + '&rdquo;</p>' : '') +
                        (item.Overview ? '<p class="moonfin-overview">' + item.Overview + '</p>' : '') +
                    '</div>' +
                    
                    '<div class="moonfin-poster-section' + (item.Type === 'Episode' ? ' moonfin-poster-landscape' : '') + '">' +
                        '<div class="moonfin-poster">' +
                            (posterUrl ? '<img src="' + posterUrl + '" alt="" loading="lazy">' : '') +
                        '</div>' +
                    '</div>' +
                '</div>' +
                
                '<div class="moonfin-actions">' +
                    actionBtns.join('') +
                '</div>' +
                
                metadataHtml +
                
                '<div class="moonfin-sections">' +
                    seasonsHtml +
                    episodesHtml +
                    
                    (cast.length > 0 ? 
                        '<div class="moonfin-section">' +
                            '<div class="moonfin-section-header">' +
                                '<h3 class="moonfin-section-title">Cast & Crew</h3>' +
                                arrowsHtml +
                            '</div>' +
                            '<div class="moonfin-section-scroll">' + castHtml + '</div>' +
                        '</div>' : '') +
                    
                    (similar.length > 0 ? 
                        '<div class="moonfin-section">' +
                            '<div class="moonfin-section-header">' +
                                '<h3 class="moonfin-section-title">More Like This</h3>' +
                                arrowsHtml +
                            '</div>' +
                            '<div class="moonfin-section-scroll">' + similarHtml + '</div>' +
                        '</div>' : '') +
                '</div>' +
            '</div>';

        this.setupPanelListeners(panel, item);
    },

    getMediaBadges: function(item) {
        var badges = [];
        
        if (item.MediaStreams) {
            var video = null;
            var audio = null;
            
            for (var i = 0; i < item.MediaStreams.length; i++) {
                if (item.MediaStreams[i].Type === 'Video' && !video) video = item.MediaStreams[i];
                if (item.MediaStreams[i].Type === 'Audio' && !audio) audio = item.MediaStreams[i];
            }
            
            if (video) {
                if (video.Width >= 3800) badges.push('<span class="moonfin-badge moonfin-badge-4k">4K</span>');
                else if (video.Width >= 1900) badges.push('<span class="moonfin-badge moonfin-badge-hd">HD</span>');
                
                if (video.VideoRange === 'HDR' || video.VideoRangeType) badges.push('<span class="moonfin-badge moonfin-badge-hdr">HDR</span>');
                
                if (video.VideoDoViTitle || (video.Title && video.Title.indexOf('Dolby Vision') !== -1)) {
                    badges.push('<span class="moonfin-badge moonfin-badge-dv">DV</span>');
                }
            }
            
            if (audio) {
                if ((audio.DisplayTitle && audio.DisplayTitle.indexOf('Atmos') !== -1) || (audio.Profile && audio.Profile.indexOf('Atmos') !== -1)) {
                    badges.push('<span class="moonfin-badge moonfin-badge-atmos">ATMOS</span>');
                } else if ((audio.DisplayTitle && audio.DisplayTitle.indexOf('DTS:X') !== -1) || (audio.Profile && audio.Profile.indexOf('DTS:X') !== -1)) {
                    badges.push('<span class="moonfin-badge moonfin-badge-dtsx">DTS:X</span>');
                } else if (audio.Channels >= 6) {
                    badges.push('<span class="moonfin-badge moonfin-badge-surround">' + (audio.Channels >= 8 ? '7.1' : '5.1') + '</span>');
                }
            }
        }
        
        return badges;
    },

    formatRuntime: function(ticks) {
        var minutes = Math.floor(ticks / 600000000);
        if (minutes < 60) return minutes + 'm';
        var hours = Math.floor(minutes / 60);
        var mins = minutes % 60;
        return mins > 0 ? hours + 'h ' + mins + 'm' : hours + 'h';
    },

    toggleFavorite: function(item) {
        var self = this;
        var api = API.getApiClient();
        var userId = api.getCurrentUserId();
        var serverUrl = this.getServerUrl();
        var headers = this.getAuthHeaders();
        var isFav = item.UserData ? item.UserData.IsFavorite : false;
        fetch(serverUrl + '/Users/' + userId + '/FavoriteItems/' + item.Id, {
            method: isFav ? 'DELETE' : 'POST',
            headers: headers
        }).then(function(resp) {
            if (resp.ok) {
                if (!item.UserData) item.UserData = {};
                item.UserData.IsFavorite = !isFav;
                var wrapper = self.container.querySelector('[data-action="favorite"]');
                if (wrapper) {
                    wrapper.classList.toggle('active');
                    var label = wrapper.querySelector('.moonfin-btn-label');
                    if (label) label.textContent = item.UserData.IsFavorite ? 'Favorited' : 'Favorite';
                }
            }
        }).catch(function(err) { console.error('[Moonfin] Details: Failed to toggle favorite', err); });
    },

    togglePlayed: function(item) {
        var self = this;
        var api = API.getApiClient();
        var userId = api.getCurrentUserId();
        var serverUrl = this.getServerUrl();
        var headers = this.getAuthHeaders();
        var isPlayed = item.UserData ? item.UserData.Played : false;
        fetch(serverUrl + '/Users/' + userId + '/PlayedItems/' + item.Id, {
            method: isPlayed ? 'DELETE' : 'POST',
            headers: headers
        }).then(function(resp) {
            if (resp.ok) {
                if (!item.UserData) item.UserData = {};
                item.UserData.Played = !isPlayed;
                var wrapper = self.container.querySelector('[data-action="played"]');
                if (wrapper) {
                    wrapper.classList.toggle('active');
                    var label = wrapper.querySelector('.moonfin-btn-label');
                    if (label) label.textContent = item.UserData.Played ? 'Watched' : 'Unwatched';
                }
            }
        }).catch(function(err) { console.error('[Moonfin] Details: Failed to toggle played', err); });
    },

    setupScrollArrows: function(panel) {
        var arrowBtns = panel.querySelectorAll('.moonfin-section-arrow');
        for (var m = 0; m < arrowBtns.length; m++) {
            (function(btn) {
                btn.addEventListener('click', function(e) {
                    e.stopPropagation();
                    var section = btn.closest('.moonfin-section');
                    if (!section) return;
                    var scrollContainer = section.querySelector('.moonfin-section-scroll');
                    if (!scrollContainer) return;
                    var scrollAmount = scrollContainer.clientWidth * 0.7;
                    var isLeft = btn.classList.contains('moonfin-arrow-left');
                    scrollContainer.scrollBy({
                        left: isLeft ? -scrollAmount : scrollAmount,
                        behavior: 'smooth'
                    });
                });
            })(arrowBtns[m]);
        }
    },

    setupPanelListeners: function(panel, item) {
        var self = this;
        
        var backBtn = panel.querySelector('.moonfin-details-back');
        if (backBtn) backBtn.addEventListener('click', function() { self.hide(); });

        var actionBtns = panel.querySelectorAll('[data-action]');
        for (var i = 0; i < actionBtns.length; i++) {
            (function(btn) {
                btn.addEventListener('click', function(e) {
                    self.handleAction(e.currentTarget.getAttribute('data-action'), item);
                });
            })(actionBtns[i]);
        }

        var similarCards = panel.querySelectorAll('.moonfin-similar-card');
        for (var j = 0; j < similarCards.length; j++) {
            (function(card) {
                card.addEventListener('click', function() {
                    self.showDetails(card.getAttribute('data-item-id'), card.getAttribute('data-type'));
                });
            })(similarCards[j]);
        }

        var seasonCards = panel.querySelectorAll('.moonfin-season-card');
        for (var k = 0; k < seasonCards.length; k++) {
            (function(card) {
                card.addEventListener('click', function() {
                    self.showDetails(card.getAttribute('data-item-id'), 'Season');
                });
            })(seasonCards[k]);
        }

        var episodeCards = panel.querySelectorAll('.moonfin-episode-card');
        for (var n = 0; n < episodeCards.length; n++) {
            (function(card) {
                card.addEventListener('click', function() {
                    var epId = card.getAttribute('data-item-id');
                    self.showDetails(epId, 'Episode');
                });
            })(episodeCards[n]);
        }

        var personCards = panel.querySelectorAll('.moonfin-cast-card');
        for (var l = 0; l < personCards.length; l++) {
            (function(card) {
                card.addEventListener('click', function() {
                    self.showDetails(card.getAttribute('data-person-id'), 'Person');
                });
            })(personCards[l]);
        }

        this.setupScrollArrows(panel);
    },

    getAuthHeaders: function() {
        var api = API.getApiClient();
        var token = api.accessToken();
        return {
            'Authorization': 'MediaBrowser Token="' + token + '"',
            'Content-Type': 'application/json'
        };
    },

    getServerUrl: function() {
        var api = API.getApiClient();
        return api._serverAddress || api.serverAddress();
    },

    getSessionId: function() {
        var api = API.getApiClient();
        var serverUrl = this.getServerUrl();
        var deviceId = api.deviceId();

        return fetch(serverUrl + '/Sessions?DeviceId=' + encodeURIComponent(deviceId), {
            headers: this.getAuthHeaders()
        }).then(function(resp) {
            return resp.json();
        }).then(function(sessions) {
            return (sessions && sessions.length > 0) ? sessions[0].Id : null;
        });
    },

    // Uses ApiClient.sendPlayCommand first, falls back to REST Sessions API
    playItem: function(itemId, startPositionTicks, audioStreamIndex, subtitleStreamIndex) {
        var self = this;
        var api = API.getApiClient();

        var playBody = {
            ItemIds: [itemId],
            PlayCommand: 'PlayNow',
            StartPositionTicks: startPositionTicks || 0
        };
        if (audioStreamIndex != null) playBody.AudioStreamIndex = audioStreamIndex;
        if (subtitleStreamIndex != null && subtitleStreamIndex !== -1) {
            playBody.SubtitleStreamIndex = subtitleStreamIndex;
        }

        // Method 1: Try ApiClient.sendPlayCommand if available
        if (api && typeof api.sendPlayCommand === 'function') {
            var deviceId = api.deviceId();
            api.getSessions({ DeviceId: deviceId }).then(function(sessions) {
                if (sessions && sessions.length > 0) {
                    return api.sendPlayCommand(sessions[0].Id, playBody);
                }
                throw new Error('No session');
            }).catch(function() {
                self._playViaSession(itemId, startPositionTicks, audioStreamIndex, subtitleStreamIndex);
            });
            return;
        }

        // Method 2: Try REST Sessions API
        self._playViaSession(itemId, startPositionTicks, audioStreamIndex, subtitleStreamIndex);
    },

    _playViaSession: function(itemId, startPositionTicks, audioStreamIndex, subtitleStreamIndex) {
        var self = this;
        var serverUrl = this.getServerUrl();
        var headers = this.getAuthHeaders();

        this.getSessionId().then(function(sessionId) {
            if (!sessionId) {
                throw new Error('No session found');
            }

            var body = {
                ItemIds: [itemId],
                StartPositionTicks: startPositionTicks || 0,
                PlayCommand: 'PlayNow'
            };
            if (audioStreamIndex != null) body.AudioStreamIndex = audioStreamIndex;
            if (subtitleStreamIndex != null && subtitleStreamIndex !== -1) {
                body.SubtitleStreamIndex = subtitleStreamIndex;
            }

            return fetch(serverUrl + '/Sessions/' + sessionId + '/Playing', {
                method: 'POST',
                headers: headers,
                body: JSON.stringify(body)
            }).then(function(resp) {
                if (!resp.ok) throw new Error('Play command failed: ' + resp.status);
            });
        }).catch(function(err) {
            console.error('[Moonfin] Details: Sessions API failed, using fallback', err);
            self._playViaFallback(itemId);
        });
    },

    // Fallback: navigate to native details page and auto-click play
    _playViaFallback: function(itemId) {
        API.navigateTo('/details?id=' + itemId);
        // Wait for the Jellyfin details page to load, then click its play button
        var attempts = 0;
        var tryClick = setInterval(function() {
            attempts++;
            var playBtn = document.querySelector('.btnPlay, .detailButton-primary, [data-action="resume"], [data-action="play"]');
            if (playBtn) {
                clearInterval(tryClick);
                playBtn.click();
            } else if (attempts > 20) {
                clearInterval(tryClick);
            }
        }, 250);
    },

    shuffleItem: function(itemId) {
        var self = this;
        var api = API.getApiClient();
        var serverUrl = this.getServerUrl();
        var headers = this.getAuthHeaders();
        var userId = api.getCurrentUserId();

        fetch(serverUrl + '/Shows/' + itemId + '/Episodes?UserId=' + userId + '&Fields=MediaSources', {
            headers: headers
        }).then(function(resp) {
            return resp.json();
        }).then(function(result) {
            var items = result.Items || [];
            if (items.length === 0) return;

            // Fisher-Yates shuffle
            var ids = items.map(function(i) { return i.Id; });
            for (var i = ids.length - 1; i > 0; i--) {
                var j = Math.floor(Math.random() * (i + 1));
                var temp = ids[i];
                ids[i] = ids[j];
                ids[j] = temp;
            }

            // Try ApiClient.sendPlayCommand first
            if (typeof api.sendPlayCommand === 'function') {
                var deviceId = api.deviceId();
                return api.getSessions({ DeviceId: deviceId }).then(function(sessions) {
                    if (sessions && sessions.length > 0) {
                        return api.sendPlayCommand(sessions[0].Id, {
                            ItemIds: ids,
                            PlayCommand: 'PlayNow',
                            StartPositionTicks: 0
                        });
                    }
                    throw new Error('No session');
                }).catch(function() {
                    return self._shuffleViaSession(ids);
                });
            }

            return self._shuffleViaSession(ids);
        }).catch(function(err) {
            console.error('[Moonfin] Details: Failed to shuffle', err);
        });
    },

    _shuffleViaSession: function(ids) {
        var self = this;
        var serverUrl = this.getServerUrl();
        var headers = this.getAuthHeaders();

        return this.getSessionId().then(function(sessionId) {
            if (!sessionId) return;

            return fetch(serverUrl + '/Sessions/' + sessionId + '/Playing', {
                method: 'POST',
                headers: headers,
                body: JSON.stringify({
                    ItemIds: ids,
                    StartPositionTicks: 0,
                    PlayCommand: 'PlayNow'
                })
            });
        });
    },

    playTrailer: function(item) {
        var self = this;
        var api = API.getApiClient();

        // Try local trailers first
        if (item.LocalTrailerCount > 0) {
            var userId = api.getCurrentUserId();
            var serverUrl = this.getServerUrl();
            var headers = this.getAuthHeaders();

            fetch(serverUrl + '/Users/' + userId + '/Items/' + item.Id + '/LocalTrailers', {
                headers: headers
            }).then(function(resp) {
                return resp.json();
            }).then(function(trailers) {
                if (trailers && trailers.length > 0) {
                    self.hide(true);
                    self.playItem(trailers[0].Id, 0);
                }
            }).catch(function(err) {
                console.error('[Moonfin] Details: Failed to load local trailers', err);
                // Fall back to remote trailers
                self.openRemoteTrailer(item);
            });
            return;
        }

        // Fall back to remote trailers
        this.openRemoteTrailer(item);
    },

    openRemoteTrailer: function(item) {
        if (item.RemoteTrailers && item.RemoteTrailers.length > 0) {
            var url = item.RemoteTrailers[0].Url;
            if (url) {
                window.open(url, '_blank', 'noopener');
            }
        }
    },

    handleAction: function(action, item) {
        switch (action) {
            case 'play':
                this.hide(true);
                var resumeTicks = (item.UserData && item.UserData.PlaybackPositionTicks) ? item.UserData.PlaybackPositionTicks : 0;
                this.playItem(item.Id, resumeTicks, this._selectedAudioIndex, this._selectedSubtitleIndex);
                break;

            case 'restart':
                this.hide(true);
                this.playItem(item.Id, 0, this._selectedAudioIndex, this._selectedSubtitleIndex);
                break;

            case 'audio':
                this.showAudioPicker(item);
                break;

            case 'subtitle':
                this.showSubtitlePicker(item);
                break;

            case 'trailer':
                this.playTrailer(item);
                break;

            case 'shuffle':
                this.hide(true);
                this.shuffleItem(item.Id);
                break;

            case 'favorite':
                this.toggleFavorite(item);
                break;

            case 'played':
                this.togglePlayed(item);
                break;

            case 'series':
                Details.showDetails(item.SeriesId, 'Series');
                break;

            case 'more':
                this.showMoreMenu(item);
                break;
        }
    },

    renderMdbListRatings: function(ratings) {
        var container = this.container.querySelector('#moonfin-details-mdblist');
        if (!container) return;

        var html = MdbList.buildRatingsHtml(ratings, 'full');
        if (html) {
            container.innerHTML = html;
            container.style.display = '';
        }
    },

    renderTmdbEpisodeRating: function(rating) {
        var container = this.container.querySelector('#moonfin-details-mdblist');
        if (!container) return;

        var html = Tmdb.buildRatingHtml(rating);
        if (html) {
            container.insertAdjacentHTML('beforeend', html);
            container.style.display = '';
        }
    },

    fetchTmdbRatingsForEpisodeList: function(item, episodes) {
        var self = this;
        if (!item.SeriesId) return;

        Tmdb.resolveSeriesTmdbId(item.SeriesId).then(function(tmdbId) {
            if (!tmdbId) return;
            var season = item.ParentIndexNumber;
            if (season == null) return;

            Tmdb.fetchSeasonRatings(tmdbId, season).then(function(tmdbEpisodes) {
                if (!tmdbEpisodes || tmdbEpisodes.length === 0) return;
                if (!self.currentItem || self.currentItem.Id !== item.Id) return;

                var ratingMap = {};
                for (var i = 0; i < tmdbEpisodes.length; i++) {
                    if (tmdbEpisodes[i].episodeNumber != null) {
                        ratingMap[tmdbEpisodes[i].episodeNumber] = tmdbEpisodes[i];
                    }
                }

                var epCards = self.container.querySelectorAll('.moonfin-episode-card');
                for (var j = 0; j < epCards.length; j++) {
                    var epId = epCards[j].getAttribute('data-item-id');
                    for (var k = 0; k < episodes.length; k++) {
                        if (episodes[k].Id === epId && episodes[k].IndexNumber != null) {
                            var tmdbRating = ratingMap[episodes[k].IndexNumber];
                            if (tmdbRating) {
                                var infoEl = epCards[j].querySelector('.moonfin-episode-info');
                                if (infoEl) {
                                    infoEl.insertAdjacentHTML('beforeend', Tmdb.buildCompactRatingHtml(tmdbRating));
                                }
                            }
                            break;
                        }
                    }
                }
            });
        });
    },

    showMoreMenu: function(item) {
        var self = this;

        this.closeMoreMenu();

        API.getCurrentUser().then(function(user) {
            self._buildMoreMenu(item, user);
        }).catch(function() {
            // Fallback: build with no user (only safe items shown)
            self._buildMoreMenu(item, null);
        });
    },

    _buildMoreMenu: function(item, user) {
        var self = this;
        var policy = (user && user.Policy) || {};
        var isAdmin = policy.IsAdministrator || false;

        var overlay = document.createElement('div');
        overlay.className = 'moonfin-more-overlay';

        var menuItems = [];

        // Add to Playlist — available for media items (has MediaType or IsFolder)
        if (item.MediaType || item.IsFolder) {
            menuItems.push({ id: 'addtoplaylist', name: 'Add to Playlist', icon: '<svg viewBox="0 -960 960 960" fill="currentColor"><path d="M480-120v-80h280v80H480Zm0-160v-80h280v80H480Zm0-160v-80h280v80H480ZM200-360v-240h80v240h-80Zm120-120v-120h80v120h-80Z"/></svg>' });
        }

        // Add to Collection — admin or user with EnableCollectionManagement, and item supports it
        var collectionInvalidTypes = ['Genre', 'MusicGenre', 'Studio', 'UserView', 'CollectionFolder', 'Audio', 'Program', 'Timer', 'SeriesTimer'];
        if ((isAdmin || policy.EnableCollectionManagement) && !item.CollectionType && collectionInvalidTypes.indexOf(item.Type) === -1) {
            menuItems.push({ id: 'addtocollection', name: 'Add to Collection', icon: '<svg viewBox="0 -960 960 960" fill="currentColor"><path d="M260-160q-91 0-155.5-63T40-377q0-78 47-139t121-71q17-91 90-147t163-56q100 0 172.5 69T707-554q71 5 122 57t51 127q0 75-52.5 127.5T700-190H260Zm0-80h440q42 0 71-29t29-71q0-42-29-71t-71-29h-60v-80q0-66-47-113t-113-47q-57 0-100 34t-56 89l-8 33h-42q-58 2-98 42.5T136-377q0 58 41 97.5t83 39.5Zm220-160Z"/></svg>' });
        }

        // Instant Mix — only for music-type items
        if (item.MediaType === 'Audio' || item.Type === 'MusicAlbum' || item.Type === 'MusicArtist' || item.Type === 'MusicGenre') {
            menuItems.push({ id: 'instantmix', name: 'Instant Mix', icon: '<svg viewBox="0 -960 960 960" fill="currentColor"><path d="M400-120q-66 0-113-47t-47-113q0-66 47-113t113-47q23 0 42.5 5.5T480-418v-422h240v160H560v400q0 66-47 113t-113 47Z"/></svg>' });
        }

        // Media Info — only if MediaSources exist
        if (item.MediaSources) {
            menuItems.push({ id: 'mediainfo', name: 'Media Info', icon: '<svg viewBox="0 -960 960 960" fill="currentColor"><path d="M440-280h80v-240h-80v240Zm40-320q17 0 28.5-11.5T520-640q0-17-11.5-28.5T480-680q-17 0-28.5 11.5T440-640q0 17 11.5 28.5T480-600Zm0 520q-83 0-156-31.5T197-197q-54-54-85.5-127T80-480q0-83 31.5-156T197-763q54-54 127-85.5T480-880q83 0 156 31.5T763-763q54 54 85.5 127T880-480q0 83-31.5 156T763-197q-54 54-127 85.5T480-80Z"/></svg>' });
        }

        // Download — requires EnableContentDownloading permission and CanDownload on item
        if (policy.EnableContentDownloading && item.CanDownload) {
            menuItems.push({ id: 'download', name: 'Download', icon: '<svg viewBox="0 -960 960 960" fill="currentColor"><path d="M480-320 280-520l56-58 104 104v-326h80v326l104-104 56 58-200 200ZM240-160q-33 0-56.5-23.5T160-240v-120h80v120h480v-120h80v120q0 33-23.5 56.5T720-160H240Z"/></svg>' });
        }

        // Delete — only if server says CanDelete is true
        if (item.CanDelete) {
            menuItems.push({ id: 'delete', name: 'Delete', icon: '<svg viewBox="0 -960 960 960" fill="currentColor"><path d="M280-120q-33 0-56.5-23.5T200-200v-520h-40v-80h200v-40h240v40h200v80h-40v520q0 33-23.5 56.5T680-120H280Zm400-600H280v520h400v-520ZM360-280h80v-360h-80v360Zm160 0h80v-360h-80v360ZM280-720v520-520Z"/></svg>', className: 'moonfin-more-item-danger' });
        }

        var hasAdminItems = false;

        // Edit Metadata — admin only
        if (isAdmin && item.Type !== 'Program' && item.Type !== 'Timer' && item.Type !== 'SeriesTimer') {
            hasAdminItems = true;
            menuItems.push({ id: 'editmetadata', name: 'Edit Metadata', icon: '<svg viewBox="0 -960 960 960" fill="currentColor"><path d="M200-200h57l391-391-57-57-391 391v57Zm-80 80v-170l528-527q12-11 26.5-17t30.5-6q16 0 31 6t26 18l55 56q12 11 17.5 26t5.5 30q0 16-5.5 30.5T817-647L290-120H120Zm640-584-56-56 56 56Zm-141 85-28-29 57 57-29-28Z"/></svg>' });
        }

        // Edit Images — admin only
        if (isAdmin && item.Type !== 'Timer' && item.Type !== 'SeriesTimer') {
            hasAdminItems = true;
            menuItems.push({ id: 'editimages', name: 'Edit Images', icon: '<svg viewBox="0 -960 960 960" fill="currentColor"><path d="M200-120q-33 0-56.5-23.5T120-200v-560q0-33 23.5-56.5T200-840h560q33 0 56.5 23.5T840-760v560q0 33-23.5 56.5T760-120H200Zm0-80h560v-560H200v560Zm40-80h480L570-480 450-320l-90-120-120 160Zm-40 80v-560 560Z"/></svg>' });
        }

        // Edit Subtitles — admin or EnableSubtitleManagement, and Video media type
        if ((isAdmin || policy.EnableSubtitleManagement) && item.MediaType === 'Video') {
            hasAdminItems = true;
            menuItems.push({ id: 'editsubtitles', name: 'Edit Subtitles', icon: '<svg viewBox="0 -960 960 960" fill="currentColor"><path d="M200-160q-33 0-56.5-23.5T120-240v-480q0-33 23.5-56.5T200-800h560q33 0 56.5 23.5T840-720v480q0 33-23.5 56.5T760-160H200Zm0-80h560v-480H200v480Zm80-120h120v-80H280v80Zm200 0h200v-80H480v80ZM280-480h200v-80H280v80Zm280 0h120v-80H560v80Z"/></svg>' });
        }

        // Identify — admin only, specific item types
        var identifyTypes = ['Movie', 'Trailer', 'Series', 'BoxSet', 'Person', 'Book', 'MusicAlbum', 'MusicArtist', 'MusicVideo'];
        if (isAdmin && identifyTypes.indexOf(item.Type) !== -1) {
            hasAdminItems = true;
            menuItems.push({ id: 'identify', name: 'Identify', icon: '<svg viewBox="0 -960 960 960" fill="currentColor"><path d="M784-120 532-372q-30 24-69 38t-83 14q-109 0-184.5-75.5T120-580q0-109 75.5-184.5T380-840q109 0 184.5 75.5T640-580q0 44-14 83t-38 69l252 252-56 56ZM380-400q75 0 127.5-52.5T560-580q0-75-52.5-127.5T380-760q-75 0-127.5 52.5T200-580q0 75 52.5 127.5T380-400Z"/></svg>' });
        }

        // Refresh Metadata — admin only
        if (isAdmin) {
            hasAdminItems = true;
            menuItems.push({ id: 'refresh', name: 'Refresh Metadata', icon: '<svg viewBox="0 -960 960 960" fill="currentColor"><path d="M480-160q-134 0-227-93t-93-227q0-134 93-227t227-93q69 0 132 28.5T720-690v-110h80v280H520v-80h168q-32-56-87.5-88T480-720q-100 0-170 70t-70 170q0 100 70 170t170 70q77 0 139-44t87-116h84q-28 106-114 173t-196 67Z"/></svg>' });
        }

        // Open in Jellyfin — always available
        menuItems.push({ id: 'opennative', name: 'Open in Jellyfin', icon: '<svg viewBox="0 -960 960 960" fill="currentColor"><path d="M200-120q-33 0-56.5-23.5T120-200v-560q0-33 23.5-56.5T200-840h280v80H200v560h560v-280h80v280q0 33-23.5 56.5T760-120H200Zm188-212-56-56 372-372H560v-80h280v280h-80v-144L388-332Z"/></svg>' });

        var menuHtml = '<div class="moonfin-more-menu">' +
            '<h3 class="moonfin-more-title">' + (item.Name || 'Options') + '</h3>' +
            '<div class="moonfin-more-items">';

        for (var i = 0; i < menuItems.length; i++) {
            menuHtml += '<button class="moonfin-more-item moonfin-focusable' + (menuItems[i].className ? ' ' + menuItems[i].className : '') + '" data-more-action="' + menuItems[i].id + '" tabindex="0">' +
                '<span class="moonfin-more-item-icon">' + menuItems[i].icon + '</span>' +
                '<span class="moonfin-more-item-text">' + menuItems[i].name + '</span>' +
            '</button>';
        }

        menuHtml += '</div></div>';
        overlay.innerHTML = menuHtml;

        overlay.addEventListener('click', function(e) {
            if (e.target === overlay) self.closeMoreMenu();
        });

        overlay._escHandler = function(e) {
            if (e.key === 'Escape' || e.keyCode === 27 || e.keyCode === 461 || e.keyCode === 10009) {
                e.preventDefault();
                e.stopPropagation();
                self.closeMoreMenu();
            }
        };
        document.addEventListener('keydown', overlay._escHandler, true);

        var buttons = overlay.querySelectorAll('[data-more-action]');
        for (var j = 0; j < buttons.length; j++) {
            (function(btn) {
                btn.addEventListener('click', function() {
                    var actionId = btn.getAttribute('data-more-action');
                    self.handleMoreAction(actionId, item);
                });
            })(buttons[j]);
        }

        document.body.appendChild(overlay);
        setTimeout(function() {
            var first = overlay.querySelector('.moonfin-more-item');
            if (first) first.focus();
        }, 50);
    },

    closeMoreMenu: function() {
        var existing = document.querySelector('.moonfin-more-overlay');
        if (existing) {
            if (existing._escHandler) {
                document.removeEventListener('keydown', existing._escHandler, true);
            }
            existing.remove();
        }
    },

    handleMoreAction: function(actionId, item) {
        var self = this;
        var api = API.getApiClient();
        var serverUrl = this.getServerUrl();
        var headers = this.getAuthHeaders();

        this.closeMoreMenu();

        switch (actionId) {
            case 'addtoplaylist':
                this.showPlaylistPicker(item);
                break;

            case 'addtocollection':
                this.showCollectionPicker(item);
                break;

            case 'mediainfo':
                this.showMediaInfo(item);
                break;

            case 'refresh':
                fetch(serverUrl + '/Items/' + item.Id + '/Refresh', {
                    method: 'POST',
                    headers: headers,
                    body: JSON.stringify({
                        Recursive: true,
                        MetadataRefreshMode: 'Default',
                        ImageRefreshMode: 'Default',
                        ReplaceAllMetadata: false,
                        ReplaceAllImages: false
                    })
                }).then(function() {
                    console.log('[Moonfin] Details: Metadata refresh queued');
                    self.showToast('Metadata refresh queued');
                }).catch(function(err) {
                    console.error('[Moonfin] Details: Failed to refresh metadata', err);
                });
                break;

            case 'instantmix':
                this.hide(true);
                var instantMixUrl = serverUrl + '/Items/' + item.Id + '/InstantMix?UserId=' + api.getCurrentUserId() + '&Limit=50';
                fetch(instantMixUrl, { headers: headers }).then(function(resp) {
                    return resp.json();
                }).then(function(result) {
                    var mixIds = (result.Items || []).map(function(i) { return i.Id; });
                    if (mixIds.length > 0) self.playItem(mixIds[0], 0);
                }).catch(function(err) {
                    console.error('[Moonfin] Details: Instant mix failed', err);
                });
                break;

            case 'download':
                var downloadUrl = serverUrl + '/Items/' + item.Id + '/Download?api_key=' + api.accessToken();
                var a = document.createElement('a');
                a.href = downloadUrl;
                a.download = item.Name || 'download';
                document.body.appendChild(a);
                a.click();
                document.body.removeChild(a);
                break;

            case 'editmetadata':
                self.hide(true);
                API.openMetadataEditor(item.Id).then(function(success) {
                    if (!success) API.navigateTo('/details?id=' + item.Id);
                });
                break;

            case 'editimages':
                self.hide(true);
                API.openImageEditor(item.Id).then(function(success) {
                    if (!success) API.navigateTo('/details?id=' + item.Id);
                });
                break;

            case 'editsubtitles':
                self.hide(true);
                API.openSubtitleEditor(item.Id).then(function(success) {
                    if (!success) API.navigateTo('/details?id=' + item.Id);
                });
                break;

            case 'identify':
                self.hide(true);
                API.openItemIdentifier(item.Id).then(function(success) {
                    if (!success) API.navigateTo('/details?id=' + item.Id);
                });
                break;

            case 'opennative':
                this.hide(true);
                API.navigateTo('/details?id=' + item.Id);
                break;

            case 'delete':
                self.confirmDelete(item);
                break;
        }
    },

    confirmDelete: function(item) {
        var self = this;
        var serverUrl = this.getServerUrl();
        var headers = this.getAuthHeaders();

        var overlay = document.createElement('div');
        overlay.className = 'moonfin-more-overlay';
        overlay.innerHTML = '<div class="moonfin-more-menu">' +
            '<h3 class="moonfin-more-title">Delete</h3>' +
            '<p style="color:rgba(255,255,255,0.7);margin:0 0 20px;text-align:center">Are you sure you want to delete<br><strong>' + (item.Name || 'this item') + '</strong>?<br><span style="color:#ff6b6b;font-size:13px">This action cannot be undone.</span></p>' +
            '<div style="display:flex;gap:12px;justify-content:center">' +
                '<button class="moonfin-more-item moonfin-focusable moonfin-delete-cancel" tabindex="0"><span class="moonfin-more-item-text">Cancel</span></button>' +
                '<button class="moonfin-more-item moonfin-focusable moonfin-more-item-danger moonfin-delete-confirm" tabindex="0"><span class="moonfin-more-item-text">Delete</span></button>' +
            '</div>' +
        '</div>';

        var closeOverlay = function() {
            if (overlay._escHandler) document.removeEventListener('keydown', overlay._escHandler, true);
            overlay.remove();
        };

        overlay._escHandler = function(e) {
            if (e.key === 'Escape' || e.keyCode === 27 || e.keyCode === 461 || e.keyCode === 10009) {
                e.preventDefault();
                e.stopPropagation();
                closeOverlay();
            }
        };
        document.addEventListener('keydown', overlay._escHandler, true);
        overlay.addEventListener('click', function(e) { if (e.target === overlay) closeOverlay(); });

        overlay.querySelector('.moonfin-delete-cancel').addEventListener('click', closeOverlay);
        overlay.querySelector('.moonfin-delete-confirm').addEventListener('click', function() {
            fetch(serverUrl + '/Items/' + item.Id, {
                method: 'DELETE',
                headers: headers
            }).then(function(resp) {
                if (resp.ok) {
                    self.showToast('Deleted successfully');
                    self.hide();
                } else {
                    self.showToast('Failed to delete - check permissions');
                }
                closeOverlay();
            }).catch(function(err) {
                console.error('[Moonfin] Details: Delete failed', err);
                self.showToast('Delete failed');
                closeOverlay();
            });
        });

        document.body.appendChild(overlay);
        setTimeout(function() { overlay.querySelector('.moonfin-delete-cancel').focus(); }, 50);
    },

    showToast: function(message) {
        var toast = document.createElement('div');
        toast.className = 'moonfin-toast';
        toast.textContent = message;
        document.body.appendChild(toast);
        setTimeout(function() { toast.classList.add('visible'); }, 10);
        setTimeout(function() {
            toast.classList.remove('visible');
            setTimeout(function() { toast.remove(); }, 300);
        }, 2500);
    },

    showAudioPicker: function(item) {
        var self = this;
        var audioTracks = (item.MediaStreams || []).filter(function(s) { return s.Type === 'Audio'; });
        if (audioTracks.length < 2) return;

        var overlay = document.createElement('div');
        overlay.className = 'moonfin-more-overlay';

        var menuHtml = '<div class="moonfin-more-menu">' +
            '<h3 class="moonfin-more-title">Audio</h3>' +
            '<div class="moonfin-more-items">';

        for (var i = 0; i < audioTracks.length; i++) {
            var isSelected = audioTracks[i].Index === self._selectedAudioIndex;
            menuHtml += '<button class="moonfin-more-item moonfin-focusable' + (isSelected ? ' active' : '') + '" data-audio-index="' + audioTracks[i].Index + '" tabindex="0">' +
                '<span class="moonfin-more-item-icon"><svg viewBox="0 -960 960 960" fill="currentColor"><path d="M400-120q-66 0-113-47t-47-113q0-66 47-113t113-47q23 0 42.5 5.5T480-418v-422h240v160H560v400q0 66-47 113t-113 47Z"/></svg></span>' +
                '<span class="moonfin-more-item-text">' + (audioTracks[i].DisplayTitle || ('Audio ' + (i + 1))) + '</span>' +
            '</button>';
        }

        menuHtml += '</div></div>';
        overlay.innerHTML = menuHtml;

        var closeOverlay = function() {
            if (overlay._escHandler) document.removeEventListener('keydown', overlay._escHandler, true);
            overlay.remove();
        };

        overlay.addEventListener('click', function(e) { if (e.target === overlay) closeOverlay(); });
        overlay._escHandler = function(e) {
            if (e.key === 'Escape' || e.keyCode === 27 || e.keyCode === 461 || e.keyCode === 10009) {
                e.preventDefault(); e.stopPropagation(); closeOverlay();
            }
        };
        document.addEventListener('keydown', overlay._escHandler, true);

        var btns = overlay.querySelectorAll('[data-audio-index]');
        for (var j = 0; j < btns.length; j++) {
            (function(btn) {
                btn.addEventListener('click', function() {
                    self._selectedAudioIndex = parseInt(btn.getAttribute('data-audio-index'), 10);
                    var audioBtn = self.container ? self.container.querySelector('[data-action="audio"]') : null;
                    if (audioBtn) {
                        var label = audioBtn.querySelector('.moonfin-btn-label');
                        if (label) label.textContent = btn.querySelector('.moonfin-more-item-text').textContent;
                    }
                    closeOverlay();
                });
            })(btns[j]);
        }

        document.body.appendChild(overlay);
        setTimeout(function() {
            var first = overlay.querySelector('.moonfin-more-item');
            if (first) first.focus();
        }, 50);
    },

    showSubtitlePicker: function(item) {
        var self = this;
        var subtitleTracks = (item.MediaStreams || []).filter(function(s) { return s.Type === 'Subtitle'; });
        if (subtitleTracks.length === 0) return;

        var overlay = document.createElement('div');
        overlay.className = 'moonfin-more-overlay';

        var menuHtml = '<div class="moonfin-more-menu">' +
            '<h3 class="moonfin-more-title">Subtitles</h3>' +
            '<div class="moonfin-more-items">';

        var offSelected = self._selectedSubtitleIndex === -1 || self._selectedSubtitleIndex == null;
        menuHtml += '<button class="moonfin-more-item moonfin-focusable' + (offSelected ? ' active' : '') + '" data-sub-index="-1" tabindex="0">' +
            '<span class="moonfin-more-item-text">Off</span>' +
        '</button>';

        for (var i = 0; i < subtitleTracks.length; i++) {
            var isSelected = subtitleTracks[i].Index === self._selectedSubtitleIndex;
            menuHtml += '<button class="moonfin-more-item moonfin-focusable' + (isSelected ? ' active' : '') + '" data-sub-index="' + subtitleTracks[i].Index + '" tabindex="0">' +
                '<span class="moonfin-more-item-icon"><svg viewBox="0 -960 960 960" fill="currentColor"><path d="M200-160q-33 0-56.5-23.5T120-240v-480q0-33 23.5-56.5T200-800h560q33 0 56.5 23.5T840-720v480q0 33-23.5 56.5T760-160H200Zm0-80h560v-480H200v480Zm80-120h120q17 0 28.5-11.5T440-400v-40h-60v20h-80v-120h80v20h60v-40q0-17-11.5-28.5T400-600H280q-17 0-28.5 11.5T240-560v160q0 17 11.5 28.5T280-360Zm280 0h120q17 0 28.5-11.5T720-400v-40h-60v20h-80v-120h80v20h60v-40q0-17-11.5-28.5T680-600H560q-17 0-28.5 11.5T520-560v160q0 17 11.5 28.5T560-360ZM200-240v-480 480Z\"/></svg></span>' +
                '<span class="moonfin-more-item-text">' + (subtitleTracks[i].DisplayTitle || ('Subtitle ' + (i + 1))) + '</span>' +
            '</button>';
        }

        menuHtml += '</div></div>';
        overlay.innerHTML = menuHtml;

        var closeOverlay = function() {
            if (overlay._escHandler) document.removeEventListener('keydown', overlay._escHandler, true);
            overlay.remove();
        };

        overlay.addEventListener('click', function(e) { if (e.target === overlay) closeOverlay(); });
        overlay._escHandler = function(e) {
            if (e.key === 'Escape' || e.keyCode === 27 || e.keyCode === 461 || e.keyCode === 10009) {
                e.preventDefault(); e.stopPropagation(); closeOverlay();
            }
        };
        document.addEventListener('keydown', overlay._escHandler, true);

        var btns = overlay.querySelectorAll('[data-sub-index]');
        for (var j = 0; j < btns.length; j++) {
            (function(btn) {
                btn.addEventListener('click', function() {
                    self._selectedSubtitleIndex = parseInt(btn.getAttribute('data-sub-index'), 10);
                    var subBtn = self.container ? self.container.querySelector('[data-action="subtitle"]') : null;
                    if (subBtn) {
                        var label = subBtn.querySelector('.moonfin-btn-label');
                        if (label) {
                            var text = btn.querySelector('.moonfin-more-item-text').textContent;
                            label.textContent = text;
                        }
                    }
                    closeOverlay();
                });
            })(btns[j]);
        }

        document.body.appendChild(overlay);
        setTimeout(function() {
            var first = overlay.querySelector('.moonfin-more-item');
            if (first) first.focus();
        }, 50);
    },

    showPlaylistPicker: function(item) {
        var self = this;
        var api = API.getApiClient();
        var userId = api.getCurrentUserId();
        var serverUrl = this.getServerUrl();
        var headers = this.getAuthHeaders();

        fetch(serverUrl + '/Users/' + userId + '/Items?IncludeItemTypes=Playlist&Recursive=true&SortBy=SortName&SortOrder=Ascending', {
            headers: headers
        }).then(function(resp) {
            return resp.json();
        }).then(function(result) {
            var playlists = result.Items || [];

            var overlay = document.createElement('div');
            overlay.className = 'moonfin-more-overlay';

            var menuHtml = '<div class="moonfin-more-menu">' +
                '<h3 class="moonfin-more-title">Add to Playlist</h3>' +
                '<div class="moonfin-more-items">';

            menuHtml += '<button class="moonfin-more-item moonfin-focusable moonfin-playlist-create" tabindex="0">' +
                '<span class="moonfin-more-item-icon"><svg viewBox="0 -960 960 960" fill="currentColor"><path d="M440-440H200v-80h240v-240h80v240h240v80H520v240h-80v-240Z"/></svg></span>' +
                '<span class="moonfin-more-item-text">New Playlist</span>' +
            '</button>';

            if (playlists.length > 0) {
                menuHtml += '<div style="border-top:1px solid rgba(255,255,255,0.1);margin:4px 0"></div>';
            }

            for (var i = 0; i < playlists.length; i++) {
                menuHtml += '<button class="moonfin-more-item moonfin-focusable" data-playlist-id="' + playlists[i].Id + '" tabindex="0">' +
                    '<span class="moonfin-more-item-icon"><svg viewBox="0 -960 960 960" fill="currentColor"><path d="M500-360q42 0 71-29t29-71q0-42-29-71t-71-29q-42 0-71 29t-29 71q0 42 29 71t71 29ZM200-120v-640h560v361q-20-2-40 1t-40 12V-680H280v368l220-140 64 41q-13 17-20.5 37T536-334l-36 22-300-190v382Z"/></svg></span>' +
                    '<span class="moonfin-more-item-text">' + playlists[i].Name + '</span>' +
                '</button>';
            }

            menuHtml += '</div></div>';
            overlay.innerHTML = menuHtml;

            var closeOverlay = function() {
                if (overlay._escHandler) document.removeEventListener('keydown', overlay._escHandler, true);
                overlay.remove();
            };

            overlay.addEventListener('click', function(e) {
                if (e.target === overlay) closeOverlay();
            });

            overlay._escHandler = function(e) {
                if (e.key === 'Escape' || e.keyCode === 27 || e.keyCode === 461 || e.keyCode === 10009) {
                    e.preventDefault();
                    e.stopPropagation();
                    closeOverlay();
                }
            };
            document.addEventListener('keydown', overlay._escHandler, true);

            var createBtn = overlay.querySelector('.moonfin-playlist-create');
            if (createBtn) {
                createBtn.addEventListener('click', function() {
                    closeOverlay();
                    self.showCreatePlaylistDialog(item);
                });
            }

            var playlistBtns = overlay.querySelectorAll('[data-playlist-id]');
            for (var j = 0; j < playlistBtns.length; j++) {
                (function(btn) {
                    btn.addEventListener('click', function() {
                        var playlistId = btn.getAttribute('data-playlist-id');
                        fetch(serverUrl + '/Playlists/' + playlistId + '/Items?Ids=' + item.Id + '&UserId=' + userId, {
                            method: 'POST',
                            headers: headers
                        }).then(function() {
                            self.showToast('Added to playlist');
                        }).catch(function(err) {
                            console.error('[Moonfin] Details: Failed to add to playlist', err);
                            self.showToast('Failed to add to playlist');
                        });
                        if (overlay._escHandler) document.removeEventListener('keydown', overlay._escHandler, true);
                        overlay.remove();
                    });
                })(playlistBtns[j]);
            }

            document.body.appendChild(overlay);
            setTimeout(function() {
                var first = overlay.querySelector('.moonfin-more-item');
                if (first) first.focus();
            }, 50);
        }).catch(function(err) {
            console.error('[Moonfin] Details: Failed to fetch playlists', err);
            self.showToast('Failed to load playlists');
        });
    },

    showCreatePlaylistDialog: function(item) {
        var self = this;
        var serverUrl = this.getServerUrl();
        var headers = this.getAuthHeaders();
        var api = API.getApiClient();
        var userId = api.getCurrentUserId();

        var overlay = document.createElement('div');
        overlay.className = 'moonfin-more-overlay';
        overlay.innerHTML = '<div class="moonfin-more-menu">' +
            '<h3 class="moonfin-more-title">New Playlist</h3>' +
            '<div style="padding:0 8px">' +
                '<input type="text" class="moonfin-playlist-name-input" placeholder="Playlist name" style="' +
                    'width:100%;box-sizing:border-box;padding:10px 14px;margin:8px 0 16px;' +
                    'background:rgba(255,255,255,0.08);border:1px solid rgba(255,255,255,0.15);border-radius:8px;' +
                    'color:#fff;font-size:15px;outline:none;font-family:inherit' +
                '" />' +
                '<div style="display:flex;gap:12px;justify-content:flex-end">' +
                    '<button class="moonfin-more-item moonfin-focusable moonfin-playlist-cancel" tabindex="0" style="flex:none;width:auto;padding:8px 20px">' +
                        '<span class="moonfin-more-item-text">Cancel</span>' +
                    '</button>' +
                    '<button class="moonfin-more-item moonfin-focusable moonfin-playlist-confirm" tabindex="0" style="flex:none;width:auto;padding:8px 20px">' +
                        '<span class="moonfin-more-item-text">Create</span>' +
                    '</button>' +
                '</div>' +
            '</div>' +
        '</div>';

        var closeOverlay = function() {
            if (overlay._escHandler) document.removeEventListener('keydown', overlay._escHandler, true);
            overlay.remove();
        };

        overlay.addEventListener('click', function(e) {
            if (e.target === overlay) closeOverlay();
        });

        overlay._escHandler = function(e) {
            if (e.key === 'Escape' || e.keyCode === 27 || e.keyCode === 461 || e.keyCode === 10009) {
                e.preventDefault();
                e.stopPropagation();
                closeOverlay();
            }
        };
        document.addEventListener('keydown', overlay._escHandler, true);

        overlay.querySelector('.moonfin-playlist-cancel').addEventListener('click', closeOverlay);

        overlay.querySelector('.moonfin-playlist-confirm').addEventListener('click', function() {
            var nameInput = overlay.querySelector('.moonfin-playlist-name-input');
            var playlistName = (nameInput.value || '').trim();
            if (!playlistName) {
                nameInput.style.borderColor = '#ff6b6b';
                nameInput.focus();
                return;
            }

            var mediaType = item.MediaType || 'Video';
            var createHeaders = Object.assign({}, headers, { 'Content-Type': 'application/json' });

            fetch(serverUrl + '/Playlists', {
                method: 'POST',
                headers: createHeaders,
                body: JSON.stringify({
                    Name: playlistName,
                    Ids: [item.Id],
                    UserId: userId,
                    MediaType: mediaType
                })
            }).then(function(resp) {
                if (!resp.ok) throw new Error('HTTP ' + resp.status);
                return resp.json();
            }).then(function() {
                self.showToast('Created playlist & added item');
                closeOverlay();
            }).catch(function(err) {
                console.error('[Moonfin] Details: Failed to create playlist', err);
                self.showToast('Failed to create playlist');
            });
        });

        var nameInput = overlay.querySelector('.moonfin-playlist-name-input');
        nameInput.addEventListener('keydown', function(e) {
            if (e.key === 'Enter') {
                e.preventDefault();
                overlay.querySelector('.moonfin-playlist-confirm').click();
            }
        });

        document.body.appendChild(overlay);
        setTimeout(function() { nameInput.focus(); }, 50);
    },

    showCollectionPicker: function(item) {
        var self = this;
        var api = API.getApiClient();
        var userId = api.getCurrentUserId();
        var serverUrl = this.getServerUrl();
        var headers = this.getAuthHeaders();

        fetch(serverUrl + '/Users/' + userId + '/Items?IncludeItemTypes=BoxSet&Recursive=true&SortBy=SortName&SortOrder=Ascending', {
            headers: headers
        }).then(function(resp) {
            return resp.json();
        }).then(function(result) {
            var collections = result.Items || [];
            if (collections.length === 0) {
                self.showToast('No collections found');
                return;
            }

            var overlay = document.createElement('div');
            overlay.className = 'moonfin-more-overlay';

            var menuHtml = '<div class="moonfin-more-menu">' +
                '<h3 class="moonfin-more-title">Add to Collection</h3>' +
                '<div class="moonfin-more-items">';

            for (var i = 0; i < collections.length; i++) {
                menuHtml += '<button class="moonfin-more-item moonfin-focusable" data-collection-id="' + collections[i].Id + '" tabindex="0">' +
                    '<span class="moonfin-more-item-icon"><svg viewBox="0 -960 960 960" fill="currentColor"><path d="M260-160q-91 0-155.5-63T40-377q0-78 47-139t121-71q17-91 90-147t163-56q100 0 172.5 69T707-554q71 5 122 57t51 127q0 75-52.5 127.5T700-190H260Z"/></svg></span>' +
                    '<span class="moonfin-more-item-text">' + collections[i].Name + '</span>' +
                '</button>';
            }

            menuHtml += '</div></div>';
            overlay.innerHTML = menuHtml;

            overlay.addEventListener('click', function(e) {
                if (e.target === overlay) {
                    if (overlay._escHandler) document.removeEventListener('keydown', overlay._escHandler, true);
                    overlay.remove();
                }
            });

            overlay._escHandler = function(e) {
                if (e.key === 'Escape' || e.keyCode === 27 || e.keyCode === 461 || e.keyCode === 10009) {
                    e.preventDefault();
                    e.stopPropagation();
                    document.removeEventListener('keydown', overlay._escHandler, true);
                    overlay.remove();
                }
            };
            document.addEventListener('keydown', overlay._escHandler, true);

            var collBtns = overlay.querySelectorAll('[data-collection-id]');
            for (var j = 0; j < collBtns.length; j++) {
                (function(btn) {
                    btn.addEventListener('click', function() {
                        var collectionId = btn.getAttribute('data-collection-id');
                        fetch(serverUrl + '/Collections/' + collectionId + '/Items?Ids=' + item.Id, {
                            method: 'POST',
                            headers: headers
                        }).then(function() {
                            self.showToast('Added to collection');
                        }).catch(function(err) {
                            console.error('[Moonfin] Details: Failed to add to collection', err);
                            self.showToast('Failed to add to collection');
                        });
                        if (overlay._escHandler) document.removeEventListener('keydown', overlay._escHandler, true);
                        overlay.remove();
                    });
                })(collBtns[j]);
            }

            document.body.appendChild(overlay);
            setTimeout(function() {
                var first = overlay.querySelector('.moonfin-more-item');
                if (first) first.focus();
            }, 50);
        }).catch(function(err) {
            console.error('[Moonfin] Details: Failed to fetch collections', err);
            self.showToast('Failed to load collections');
        });
    },

    showMediaInfo: function(item) {
        var self = this;
        var streams = item.MediaStreams || [];

        var overlay = document.createElement('div');
        overlay.className = 'moonfin-more-overlay';

        var infoHtml = '<div class="moonfin-more-menu moonfin-media-info-menu">' +
            '<h3 class="moonfin-more-title">Media Info</h3>' +
            '<div class="moonfin-media-info-content">';

        if (streams.length === 0) {
            infoHtml += '<p class="moonfin-media-info-empty">No media info available</p>';
        } else {
            for (var i = 0; i < streams.length; i++) {
                var s = streams[i];
                infoHtml += '<div class="moonfin-media-info-stream">';
                infoHtml += '<div class="moonfin-media-info-stream-header">' + s.Type + (s.Language ? ' (' + s.Language + ')' : '') + '</div>';

                if (s.Type === 'Video') {
                    if (s.DisplayTitle) infoHtml += '<div class="moonfin-media-info-row">' + s.DisplayTitle + '</div>';
                    var details = [];
                    if (s.Width && s.Height) details.push(s.Width + 'x' + s.Height);
                    if (s.Codec) details.push(s.Codec.toUpperCase());
                    if (s.BitRate) details.push(Math.round(s.BitRate / 1000000) + ' Mbps');
                    if (s.VideoRange) details.push(s.VideoRange);
                    if (details.length) infoHtml += '<div class="moonfin-media-info-row">' + details.join(' · ') + '</div>';
                } else if (s.Type === 'Audio') {
                    if (s.DisplayTitle) infoHtml += '<div class="moonfin-media-info-row">' + s.DisplayTitle + '</div>';
                    var aDetails = [];
                    if (s.Codec) aDetails.push(s.Codec.toUpperCase());
                    if (s.Channels) aDetails.push(s.Channels + ' ch');
                    if (s.SampleRate) aDetails.push(s.SampleRate + ' Hz');
                    if (s.BitRate) aDetails.push(Math.round(s.BitRate / 1000) + ' kbps');
                    if (aDetails.length) infoHtml += '<div class="moonfin-media-info-row">' + aDetails.join(' · ') + '</div>';
                } else if (s.Type === 'Subtitle') {
                    var subDetails = [];
                    if (s.DisplayTitle) subDetails.push(s.DisplayTitle);
                    else if (s.Title) subDetails.push(s.Title);
                    if (s.Codec) subDetails.push(s.Codec.toUpperCase());
                    if (subDetails.length) infoHtml += '<div class="moonfin-media-info-row">' + subDetails.join(' · ') + '</div>';
                }

                infoHtml += '</div>';
            }

            if (item.Container) {
                infoHtml += '<div class="moonfin-media-info-stream">';
                infoHtml += '<div class="moonfin-media-info-stream-header">Container</div>';
                infoHtml += '<div class="moonfin-media-info-row">' + item.Container.toUpperCase() + '</div>';
                infoHtml += '</div>';
            }
        }

        infoHtml += '</div>' +
            '<button class="moonfin-more-item moonfin-focusable moonfin-media-info-close" tabindex="0">' +
                '<span class="moonfin-more-item-text">Close</span>' +
            '</button>' +
        '</div>';

        overlay.innerHTML = infoHtml;

        var closeMenu = function() {
            if (overlay._escHandler) document.removeEventListener('keydown', overlay._escHandler, true);
            overlay.remove();
        };

        overlay.addEventListener('click', function(e) {
            if (e.target === overlay) closeMenu();
        });

        overlay._escHandler = function(e) {
            if (e.key === 'Escape' || e.keyCode === 27 || e.keyCode === 461 || e.keyCode === 10009) {
                e.preventDefault();
                e.stopPropagation();
                closeMenu();
            }
        };
        document.addEventListener('keydown', overlay._escHandler, true);

        var closeBtn = overlay.querySelector('.moonfin-media-info-close');
        if (closeBtn) closeBtn.addEventListener('click', closeMenu);

        document.body.appendChild(overlay);
        setTimeout(function() {
            if (closeBtn) closeBtn.focus();
        }, 50);
    },

    renderSeasonDetails: function(item, episodes) {
        var self = this;
        var panel = this.container.querySelector('.moonfin-details-panel');
        var api = API.getApiClient();
        var serverUrl = api._serverAddress;

        var backdropId = item.ParentBackdropItemId || item.Id;
        var backdropUrl = serverUrl + '/Items/' + backdropId + '/Images/Backdrop?maxWidth=1920&quality=90';

        var posterTag = item.ImageTags ? item.ImageTags.Primary : null;
        var posterUrl = posterTag ? serverUrl + '/Items/' + item.Id + '/Images/Primary?maxHeight=500&quality=90' : '';

        var isPlayed = item.UserData && item.UserData.Played;
        var isFavorite = item.UserData && item.UserData.IsFavorite;

        var firstUnwatched = null;
        for (var e = 0; e < episodes.length; e++) {
            if (!episodes[e].UserData || !episodes[e].UserData.Played) {
                firstUnwatched = episodes[e];
                break;
            }
        }

        var seasonActions = '';
        if (episodes.length > 0) {
            seasonActions += '<div class="moonfin-btn-wrapper moonfin-focusable" data-action="play" tabindex="0">' +
                '<div class="moonfin-btn-circle moonfin-btn-primary">' +
                    '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M8 5v14l11-7z"/></svg>' +
                '</div>' +
                '<span class="moonfin-btn-label">Play</span>' +
            '</div>';

            seasonActions += '<div class="moonfin-btn-wrapper moonfin-focusable" data-action="shuffle" tabindex="0">' +
                '<div class="moonfin-btn-circle">' +
                    '<svg viewBox="0 -960 960 960" fill="currentColor"><path d="M560-160v-80h104L537-367l57-57 126 126v-102h80v240H560Zm-344 0-56-56 504-504H560v-80h240v240h-80v-104L216-160Zm151-377L160-744l56-56 207 207-56 56Z"/></svg>' +
                '</div>' +
                '<span class="moonfin-btn-label">Shuffle</span>' +
            '</div>';
        }

        seasonActions += '<div class="moonfin-btn-wrapper moonfin-focusable ' + (isPlayed ? 'active' : '') + '" data-action="played" tabindex="0">' +
            '<div class="moonfin-btn-circle">' +
                '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M21 7L9 19l-5.5-5.5 1.41-1.41L9 16.17 19.59 5.59 21 7z"/></svg>' +
            '</div>' +
            '<span class="moonfin-btn-label">' + (isPlayed ? 'Watched' : 'Unwatched') + '</span>' +
        '</div>';

        seasonActions += '<div class="moonfin-btn-wrapper moonfin-focusable ' + (isFavorite ? 'active' : '') + '" data-action="favorite" tabindex="0">' +
            '<div class="moonfin-btn-circle">' +
                '<svg viewBox="0 -960 960 960" fill="currentColor"><path d="' + (isFavorite ?
                    'm480-120-58-52q-101-91-167-157T150-447.5Q111-500 95.5-544T80-634q0-94 63-157t157-63q52 0 99 22t81 62q34-40 81-62t99-22q94 0 157 63t63 157q0 46-15.5 90T810-447.5Q771-395 705-329T538-172l-58 52Z' :
                    'M480-120q-14 0-28.5-5T426-140q-43-38-97.5-82.5T232-308q-41.5-41.5-72-83T122-475q-8-32-11-60.5T108-596q0-86 57-147t147-61q52 0 99 22t69 62q22-40 69-62t99-22q90 0 147 61t57 147q0 32-3 60.5T837-475q-7 42-37.5 83.5T728-308q-42 42-96.5 86.5T534-140q-11 10-25.5 15t-28.5 5Zm0-80q41-37 88.5-75t83-68.5q35.5-30.5 61-58T746-456q9-27 11.5-49t2.5-43q0-53-34.5-91.5T636-678q-43 0-77.5 24T507-602h-54q-17-28-51.5-52T324-678q-55 0-89.5 38.5T200-548q0 21 2.5 43t11.5 49q9 27 34.5 54.5t61 58Q345-313 392.5-275T480-200Z') +
                '"/></svg>' +
            '</div>' +
            '<span class="moonfin-btn-label">' + (isFavorite ? 'Favorited' : 'Favorite') + '</span>' +
        '</div>';

        seasonActions += '<div class="moonfin-btn-wrapper moonfin-focusable" data-action="more" tabindex="0">' +
            '<div class="moonfin-btn-circle">' +
                '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M12 8c1.1 0 2-.9 2-2s-.9-2-2-2-2 .9-2 2 .9 2 2 2zm0 2c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2zm0 6c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2z"/></svg>' +
            '</div>' +
            '<span class="moonfin-btn-label">More</span>' +
        '</div>';

        var episodeListHtml = episodes.map(function(ep) {
            var epThumbTag = ep.ImageTags ? ep.ImageTags.Primary : null;
            var epThumbUrl = epThumbTag ? serverUrl + '/Items/' + ep.Id + '/Images/Primary?maxWidth=400&quality=80' : '';
            var epRuntime = ep.RunTimeTicks ? self.formatRuntime(ep.RunTimeTicks) : '';
            var epProgress = ep.UserData ? ep.UserData.PlayedPercentage : 0;
            var isPlayed = ep.UserData && ep.UserData.Played;

            return '<div class="moonfin-season-ep moonfin-focusable" data-item-id="' + ep.Id + '" data-type="Episode" tabindex="0">' +
                '<div class="moonfin-season-ep-thumb">' +
                    (epThumbUrl ? '<img src="' + epThumbUrl + '" alt="" loading="lazy">' : '<div class="moonfin-season-ep-thumb-placeholder"><svg viewBox="0 0 24 24" fill="currentColor"><path d="M21 3H3c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h18c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm0 16H3V5h18v14zM9.5 7.5l7 4.5-7 4.5z"/></svg></div>') +
                    (epProgress ? '<div class="moonfin-episode-progress"><div class="moonfin-episode-progress-bar" style="width:' + Math.min(epProgress, 100) + '%"></div></div>' : '') +
                '</div>' +
                '<div class="moonfin-season-ep-body">' +
                    '<div class="moonfin-season-ep-top">' +
                        '<span class="moonfin-season-ep-number">Episode ' + (ep.IndexNumber || '?') + '</span>' +
                        '<span class="moonfin-season-ep-meta">' +
                            (epRuntime ? '<span>' + epRuntime + '</span>' : '') +
                            (isPlayed ? '<svg viewBox="0 0 24 24" fill="currentColor" width="16" height="16" class="moonfin-season-ep-check"><path d="M21 7L9 19l-5.5-5.5 1.41-1.41L9 16.17 19.59 5.59 21 7z"/></svg>' : '') +
                        '</span>' +
                    '</div>' +
                    '<span class="moonfin-season-ep-title">' + ep.Name + '</span>' +
                    (ep.Overview ? '<p class="moonfin-season-ep-overview">' + ep.Overview + '</p>' : '') +
                '</div>' +
            '</div>';
        }).join('');

        panel.innerHTML =
            '<div class="moonfin-details-backdrop" style="background-image: url(\'' + backdropUrl + '\')"></div>' +
            '<div class="moonfin-details-gradient"></div>' +

            '<button class="moonfin-details-back moonfin-focusable" title="Back" tabindex="0">' +
                '<svg viewBox="0 0 24 24"><path fill="currentColor" d="M20 11H7.83l5.59-5.59L12 4l-8 8 8 8 1.41-1.41L7.83 13H20v-2z"/></svg>' +
            '</button>' +

            '<div class="moonfin-details-content">' +
                '<div class="moonfin-season-detail-header">' +
                    '<div class="moonfin-season-detail-poster">' +
                        (posterUrl ? '<img src="' + posterUrl + '" alt="">' : '') +
                    '</div>' +
                    '<div class="moonfin-season-detail-info">' +
                        (item.SeriesName ? '<span class="moonfin-season-detail-series">' + item.SeriesName + '</span>' : '') +
                        '<h1 class="moonfin-season-detail-title">' + item.Name + '</h1>' +
                        '<span class="moonfin-season-detail-count">' + episodes.length + ' Episode' + (episodes.length !== 1 ? 's' : '') + '</span>' +
                    '</div>' +
                '</div>' +
                '<div class="moonfin-actions">' + seasonActions + '</div>' +
                '<div class="moonfin-season-episodes-list">' +
                    episodeListHtml +
                '</div>' +
            '</div>';

        this.setupSeasonPanelListeners(panel, item, episodes);

        if (Tmdb.isEnabled() && item.SeriesId) {
            this.fetchTmdbRatingsForSeasonView(item, episodes);
        }
    },

    fetchTmdbRatingsForSeasonView: function(item, episodes) {
        var self = this;
        Tmdb.resolveSeriesTmdbId(item.SeriesId).then(function(tmdbId) {
            if (!tmdbId) return;
            var season = item.IndexNumber;
            if (season == null) return;

            Tmdb.fetchSeasonRatings(tmdbId, season).then(function(tmdbEpisodes) {
                if (!tmdbEpisodes || tmdbEpisodes.length === 0) return;
                if (!self.currentItem || self.currentItem.Id !== item.Id) return;

                var ratingMap = {};
                for (var i = 0; i < tmdbEpisodes.length; i++) {
                    if (tmdbEpisodes[i].episodeNumber != null) {
                        ratingMap[tmdbEpisodes[i].episodeNumber] = tmdbEpisodes[i];
                    }
                }

                var epCards = self.container.querySelectorAll('.moonfin-season-ep');
                for (var j = 0; j < epCards.length; j++) {
                    var epId = epCards[j].getAttribute('data-item-id');
                    for (var k = 0; k < episodes.length; k++) {
                        if (episodes[k].Id === epId && episodes[k].IndexNumber != null) {
                            var tmdbRating = ratingMap[episodes[k].IndexNumber];
                            if (tmdbRating) {
                                var metaEl = epCards[j].querySelector('.moonfin-season-ep-meta');
                                if (metaEl) {
                                    metaEl.insertAdjacentHTML('afterbegin', Tmdb.buildCompactRatingHtml(tmdbRating));
                                }
                            }
                            break;
                        }
                    }
                }
            });
        });
    },

    setupSeasonPanelListeners: function(panel, item, episodes) {
        var self = this;

        var backBtn = panel.querySelector('.moonfin-details-back');
        if (backBtn) {
            backBtn.addEventListener('click', function() {
                if (item.SeriesId) {
                    self.showDetails(item.SeriesId, 'Series');
                } else {
                    self.hide();
                }
            });
        }

        var actionBtns = panel.querySelectorAll('[data-action]');
        for (var j = 0; j < actionBtns.length; j++) {
            (function(btn) {
                btn.addEventListener('click', function() {
                    var action = btn.getAttribute('data-action');
                    self.handleSeasonAction(action, item, episodes);
                });
            })(actionBtns[j]);
        }

        var episodeCards = panel.querySelectorAll('.moonfin-season-ep');
        for (var i = 0; i < episodeCards.length; i++) {
            (function(card) {
                card.addEventListener('click', function() {
                    self.showDetails(card.getAttribute('data-item-id'), 'Episode');
                });
            })(episodeCards[i]);
        }
    },

    handleSeasonAction: function(action, item, episodes) {
        var self = this;
        var api = API.getApiClient();

        switch (action) {
            case 'play':
                if (episodes.length === 0) return;
                var firstUnwatched = null;
                for (var i = 0; i < episodes.length; i++) {
                    if (!episodes[i].UserData || !episodes[i].UserData.Played) {
                        firstUnwatched = episodes[i];
                        break;
                    }
                }
                var playTarget = firstUnwatched || episodes[0];
                self.hide(true);
                self.playItem(playTarget.Id, playTarget.UserData && playTarget.UserData.PlaybackPositionTicks ? playTarget.UserData.PlaybackPositionTicks : 0);
                break;

            case 'shuffle':
                if (episodes.length === 0) return;
                self.hide(true);
                var ids = episodes.map(function(ep) { return ep.Id; });
                for (var s = ids.length - 1; s > 0; s--) {
                    var r = Math.floor(Math.random() * (s + 1));
                    var temp = ids[s];
                    ids[s] = ids[r];
                    ids[r] = temp;
                }
                if (typeof api.sendPlayCommand === 'function') {
                    var deviceId = api.deviceId();
                    api.getSessions({ DeviceId: deviceId }).then(function(sessions) {
                        if (sessions && sessions.length > 0) {
                            return api.sendPlayCommand(sessions[0].Id, {
                                ItemIds: ids,
                                PlayCommand: 'PlayNow',
                                StartPositionTicks: 0
                            });
                        }
                        throw new Error('No session');
                    }).catch(function() {
                        self._shuffleViaSession(ids);
                    });
                } else {
                    self._shuffleViaSession(ids);
                }
                break;

            case 'favorite':
                this.toggleFavorite(item);
                break;

            case 'played':
                this.togglePlayed(item);
                break;

            case 'more':
                self.showMoreMenu(item);
                break;
        }
    },

    renderPersonDetails: function(item, personItems) {
        var self = this;
        var panel = this.container.querySelector('.moonfin-details-panel');
        var api = API.getApiClient();
        var serverUrl = api._serverAddress;

        var photoTag = item.ImageTags ? item.ImageTags.Primary : null;
        var photoUrl = photoTag ? serverUrl + '/Items/' + item.Id + '/Images/Primary?maxHeight=500&quality=90' : '';

        var birthDate = item.PremiereDate ? new Date(item.PremiereDate) : null;
        var deathDate = item.EndDate ? new Date(item.EndDate) : null;
        var birthPlace = item.ProductionLocations && item.ProductionLocations.length > 0 ? item.ProductionLocations[0] : '';

        var infoItems = [];
        if (birthDate) {
            var birthStr = birthDate.toLocaleDateString('en-US', { month: 'long', day: 'numeric', year: 'numeric' });
            if (deathDate) {
                var deathStr = deathDate.toLocaleDateString('en-US', { month: 'long', day: 'numeric', year: 'numeric' });
                infoItems.push('<span class="moonfin-info-item">' + birthStr + ' — ' + deathStr + '</span>');
            } else {
                var age = Math.floor((Date.now() - birthDate.getTime()) / 31557600000);
                infoItems.push('<span class="moonfin-info-item">Born ' + birthStr + ' (age ' + age + ')</span>');
            }
        }
        if (birthPlace) {
            infoItems.push('<span class="moonfin-info-item">' + birthPlace + '</span>');
        }
        var infoRowHtml = infoItems.length > 0 ? '<div class="moonfin-info-row">' + infoItems.join('') + '</div>' : '';

        var movies = [];
        var series = [];
        for (var i = 0; i < personItems.length; i++) {
            if (personItems[i].Type === 'Movie') movies.push(personItems[i]);
            else if (personItems[i].Type === 'Series') series.push(personItems[i]);
        }

        var buildFilmCards = function(items) {
            return items.map(function(fi) {
                var fiPosterTag = fi.ImageTags ? fi.ImageTags.Primary : null;
                var fiPosterUrl = fiPosterTag ? serverUrl + '/Items/' + fi.Id + '/Images/Primary?maxHeight=400&quality=80' : '';
                var fiYear = fi.ProductionYear || (fi.PremiereDate ? new Date(fi.PremiereDate).getFullYear() : '');
                return '<div class="moonfin-similar-card moonfin-focusable" data-item-id="' + fi.Id + '" data-type="' + fi.Type + '" tabindex="0">' +
                    '<div class="moonfin-similar-poster">' +
                        (fiPosterUrl ? '<img src="' + fiPosterUrl + '" alt="" loading="lazy">' : '<div class="moonfin-poster-placeholder"><svg viewBox="0 0 24 24" fill="currentColor"><path d="M21 3H3c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h18c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm0 16H3V5h18v14z"/></svg></div>') +
                    '</div>' +
                    '<span class="moonfin-similar-title">' + fi.Name + '</span>' +
                    (fiYear ? '<span class="moonfin-person-film-year">' + fiYear + '</span>' : '') +
                '</div>';
            }).join('');
        };

        var moviesHtml = movies.length > 0 ? (
            '<div class="moonfin-section">' +
                '<div class="moonfin-section-header">' +
                    '<h3 class="moonfin-section-title">Movies (' + movies.length + ')</h3>' +
                    '<div class="moonfin-section-arrows">' +
                        '<button class="moonfin-section-arrow moonfin-arrow-left" aria-label="Scroll left"><svg viewBox="0 0 24 24" fill="currentColor"><path d="M15.41 7.41L14 6l-6 6 6 6 1.41-1.41L10.83 12z"/></svg></button>' +
                        '<button class="moonfin-section-arrow moonfin-arrow-right" aria-label="Scroll right"><svg viewBox="0 0 24 24" fill="currentColor"><path d="M10 6L8.59 7.41 13.17 12l-4.58 4.59L10 18l6-6z"/></svg></button>' +
                    '</div>' +
                '</div>' +
                '<div class="moonfin-section-scroll">' + buildFilmCards(movies) + '</div>' +
            '</div>'
        ) : '';

        var seriesHtml = series.length > 0 ? (
            '<div class="moonfin-section">' +
                '<div class="moonfin-section-header">' +
                    '<h3 class="moonfin-section-title">Series (' + series.length + ')</h3>' +
                    '<div class="moonfin-section-arrows">' +
                        '<button class="moonfin-section-arrow moonfin-arrow-left" aria-label="Scroll left"><svg viewBox="0 0 24 24" fill="currentColor"><path d="M15.41 7.41L14 6l-6 6 6 6 1.41-1.41L10.83 12z"/></svg></button>' +
                        '<button class="moonfin-section-arrow moonfin-arrow-right" aria-label="Scroll right"><svg viewBox="0 0 24 24" fill="currentColor"><path d="M10 6L8.59 7.41 13.17 12l-4.58 4.59L10 18l6-6z"/></svg></button>' +
                    '</div>' +
                '</div>' +
                '<div class="moonfin-section-scroll">' + buildFilmCards(series) + '</div>' +
            '</div>'
        ) : '';

        panel.innerHTML =
            '<div class="moonfin-details-backdrop moonfin-person-backdrop"></div>' +
            '<div class="moonfin-details-gradient"></div>' +

            '<button class="moonfin-details-back moonfin-focusable" title="Back" tabindex="0">' +
                '<svg viewBox="0 0 24 24"><path fill="currentColor" d="M20 11H7.83l5.59-5.59L12 4l-8 8 8 8 1.41-1.41L7.83 13H20v-2z"/></svg>' +
            '</button>' +

            '<div class="moonfin-details-content">' +
                '<div class="moonfin-person-header">' +
                    '<div class="moonfin-person-photo-wrapper">' +
                        (photoUrl ? '<img class="moonfin-person-photo" src="' + photoUrl + '" alt="">' : '<div class="moonfin-person-photo moonfin-person-photo-placeholder"><svg viewBox="0 0 24 24"><path fill="currentColor" d="M12 4a4 4 0 0 1 4 4 4 4 0 0 1-4 4 4 4 0 0 1-4-4 4 4 0 0 1 4-4m0 10c4.42 0 8 1.79 8 4v2H4v-2c0-2.21 3.58-4 8-4"/></svg></div>') +
                    '</div>' +
                    '<div class="moonfin-person-info">' +
                        '<h1 class="moonfin-title">' + item.Name + '</h1>' +
                        infoRowHtml +
                        (item.Overview ? '<p class="moonfin-overview">' + item.Overview + '</p>' : '') +
                    '</div>' +
                '</div>' +

                '<div class="moonfin-sections">' +
                    moviesHtml +
                    seriesHtml +
                '</div>' +
            '</div>';

        this.setupPersonPanelListeners(panel, item);
    },

    setupPersonPanelListeners: function(panel, item) {
        var self = this;

        var backBtn = panel.querySelector('.moonfin-details-back');
        if (backBtn) backBtn.addEventListener('click', function() { self.hide(); });

        var filmCards = panel.querySelectorAll('.moonfin-similar-card');
        for (var i = 0; i < filmCards.length; i++) {
            (function(card) {
                card.addEventListener('click', function() {
                    self.showDetails(card.getAttribute('data-item-id'), card.getAttribute('data-type'));
                });
            })(filmCards[i]);
        }

        this.setupScrollArrows(panel);
    },

    hide: function(skipHistoryBack) {
        if (!this.isVisible) return;
        this.container.classList.remove('visible');
        this.isVisible = false;
        this.currentItem = null;
        document.body.classList.remove('moonfin-details-visible');

        // Pop the history entry we pushed, unless caller will handle navigation
        if (!skipHistoryBack) {
            try { history.back(); } catch(e) {}
        }
    }
};
