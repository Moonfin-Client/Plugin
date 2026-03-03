var Genres = {
    container: null,
    isVisible: false,
    currentView: 'grid',   // 'grid' or 'browse'
    genres: [],
    selectedGenre: null,
    browseItems: [],
    browseTotalCount: 0,
    sortBy: 'SortName',
    sortOrder: 'Ascending',
    filterType: 'all',
    startLetter: null,
    loading: false,
    browseLoading: false,
    browseStartIndex: 0,

    SORT_OPTIONS: [
        { key: 'SortName,Ascending', label: 'Name (A-Z)' },
        { key: 'SortName,Descending', label: 'Name (Z-A)' },
        { key: 'CommunityRating,Descending', label: 'Rating' },
        { key: 'DateCreated,Descending', label: 'Date Added' },
        { key: 'PremiereDate,Descending', label: 'Release Date' },
        { key: 'Random,Ascending', label: 'Random' }
    ],

    FILTER_OPTIONS: [
        { key: 'all', label: 'All' },
        { key: 'Movie', label: 'Movies' },
        { key: 'Series', label: 'TV Shows' }
    ],

    LETTERS: ['#', 'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J', 'K', 'L', 'M', 'N', 'O', 'P', 'Q', 'R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z'],

    BATCH_SIZE: 60,

    init: function() {
        this.createContainer();
    },

    createContainer: function() {
        var existing = document.querySelector('.moonfin-genres-overlay');
        if (existing) existing.remove();

        this.container = document.createElement('div');
        this.container.className = 'moonfin-genres-overlay';
        document.body.appendChild(this.container);
    },

    show: function() {
        if (!this.container) this.createContainer();

        this.currentView = 'grid';
        this.selectedGenre = null;
        this.isVisible = true;
        this.container.classList.add('visible');
        document.body.style.overflow = 'hidden';
        history.pushState({ moonfinGenres: true }, '');
        if (window.Moonfin && window.Moonfin.Plugin) window.Moonfin.Plugin._overlayHistoryDepth++;
        else if (typeof Plugin !== 'undefined') Plugin._overlayHistoryDepth++;

        this.loadGenres();
    },

    hide: function() {
        if (this.currentView === 'browse') {
            this.showGrid();
            return;
        }

        this.isVisible = false;
        this.container.classList.remove('visible');
        document.body.style.overflow = '';
        try { history.back(); } catch(e) {}
    },

    close: function() {
        this.isVisible = false;
        if (this.container) this.container.classList.remove('visible');
        document.body.style.overflow = '';
    },

    showGrid: function() {
        this.currentView = 'grid';
        this.selectedGenre = null;
        this.renderGrid();
    },

    async loadGenres() {
        this.loading = true;
        this.renderGrid();

        try {
            var genreList = await API.getGenres();
            if (!genreList || genreList.length === 0) {
                this.genres = [];
                this.loading = false;
                this.renderGrid();
                return;
            }

            var self = this;
            var enriched = [];
            var batchSize = 8;

            for (var i = 0; i < genreList.length; i += batchSize) {
                var batch = genreList.slice(i, i + batchSize);
                var promises = batch.map(function(genre) {
                    return API.getGenreItems(genre.Name, {
                        limit: 3,
                        sortBy: 'Random',
                        includeItemTypes: 'Movie,Series'
                    }).then(function(result) {
                        var backdropUrl = null;
                        var items = result.Items || [];
                        for (var j = 0; j < items.length; j++) {
                            backdropUrl = API.getBackdropUrl(items[j], { maxWidth: 780, quality: 80 });
                            if (backdropUrl) break;
                        }
                        return {
                            id: genre.Id,
                            name: genre.Name,
                            itemCount: result.TotalRecordCount || 0,
                            backdropUrl: backdropUrl
                        };
                    }).catch(function() {
                        return {
                            id: genre.Id,
                            name: genre.Name,
                            itemCount: 0,
                            backdropUrl: null
                        };
                    });
                });

                var batchResults = await Promise.all(promises);
                enriched = enriched.concat(batchResults);
            }

            enriched.sort(function(a, b) { return a.name.localeCompare(b.name); });
            self.genres = enriched;
            self.loading = false;
            self.renderGrid();
        } catch (e) {
            console.error('[Moonfin] Failed to load genres:', e);
            this.loading = false;
            this.genres = [];
            this.renderGrid();
        }
    },

    renderGrid: function() {
        if (!this.container) return;

        var self = this;
        var html = '';

        html += '<div class="moonfin-genres-header">';
        html += '  <div class="moonfin-genres-title-section">';
        html += '    <h1 class="moonfin-genres-title">Genres</h1>';
        html += '    <span class="moonfin-genres-count">' + this.genres.length + ' genres</span>';
        html += '  </div>';
        html += '</div>';

        if (this.loading) {
            html += '<div class="moonfin-genres-loading"><div class="moonfin-genres-spinner"></div></div>';
        } else if (this.genres.length === 0) {
            html += '<div class="moonfin-genres-empty">No genres found</div>';
        } else {
            html += '<div class="moonfin-genres-grid">';
            for (var i = 0; i < this.genres.length; i++) {
                var genre = this.genres[i];
                html += '<div class="moonfin-genre-card" data-genre-index="' + i + '">';
                html += '  <div class="moonfin-genre-backdrop">';
                if (genre.backdropUrl) {
                    html += '    <img class="moonfin-genre-backdrop-img" src="' + genre.backdropUrl + '" alt="" loading="lazy">';
                } else {
                    html += '    <div class="moonfin-genre-backdrop-placeholder"></div>';
                }
                html += '    <div class="moonfin-genre-backdrop-overlay"></div>';
                html += '  </div>';
                html += '  <div class="moonfin-genre-info">';
                html += '    <div class="moonfin-genre-name">' + genre.name + '</div>';
                if (genre.itemCount > 0) {
                    html += '    <div class="moonfin-genre-item-count">' + genre.itemCount + ' items</div>';
                }
                html += '  </div>';
                html += '</div>';
            }
            html += '</div>';
        }

        this.container.innerHTML = html;
        this.bindGridEvents();
    },

    bindGridEvents: function() {
        var self = this;

        var cards = this.container.querySelectorAll('.moonfin-genre-card');
        for (var i = 0; i < cards.length; i++) {
            cards[i].addEventListener('click', function() {
                var index = parseInt(this.dataset.genreIndex, 10);
                var genre = self.genres[index];
                if (genre) {
                    self.openGenre(genre);
                }
            });
        }

        if (this._escHandler) {
            document.removeEventListener('keydown', this._escHandler);
        }
        this._escHandler = function(e) {
            if (e.key === 'Escape' && self.isVisible) {
                e.preventDefault();
                e.stopPropagation();
                self.hide();
            }
        };
        document.addEventListener('keydown', this._escHandler);
    },

    openGenre: function(genre) {
        this.currentView = 'browse';
        this.selectedGenre = genre;
        this.browseItems = [];
        this.browseTotalCount = 0;
        this.browseStartIndex = 0;
        this.sortBy = 'SortName';
        this.sortOrder = 'Ascending';
        this.filterType = 'all';
        this.startLetter = null;

        this.loadBrowseItems(true);
    },

    async loadBrowseItems(isReset) {
        if (isReset) {
            this.browseStartIndex = 0;
            this.browseItems = [];
            this.browseLoading = true;
            this.renderBrowse();
        }

        var includeItemTypes = this.filterType === 'all' ? 'Movie,Series' : this.filterType;
        var options = {
            startIndex: this.browseStartIndex,
            limit: this.BATCH_SIZE,
            sortBy: this.sortBy,
            sortOrder: this.sortOrder,
            includeItemTypes: includeItemTypes
        };

        if (this.startLetter) {
            if (this.startLetter === '#') {
                options.nameLessThan = 'A';
            } else {
                options.nameStartsWith = this.startLetter;
            }
        }

        try {
            var result = await API.getGenreItems(this.selectedGenre.name, options);
            this.browseTotalCount = result.TotalRecordCount || 0;

            if (isReset) {
                this.browseItems = result.Items || [];
            } else {
                this.browseItems = this.browseItems.concat(result.Items || []);
            }

            this.browseLoading = false;
            this.renderBrowse();
        } catch (e) {
            console.error('[Moonfin] Failed to load browse items:', e);
            this.browseLoading = false;
            this.renderBrowse();
        }
    },

    renderBrowse: function() {
        if (!this.container) return;
        var self = this;

        var currentSortKey = this.sortBy + ',' + this.sortOrder;
        var currentSort = this.SORT_OPTIONS.find(function(o) { return o.key === currentSortKey; });
        var currentFilter = this.FILTER_OPTIONS.find(function(o) { return o.key === self.filterType; });

        var html = '';

        html += '<div class="moonfin-genres-header">';
        html += '  <div class="moonfin-genres-title-section">';
        html += '    <h1 class="moonfin-genres-title">' + this.selectedGenre.name + '</h1>';
        html += '    <span class="moonfin-genres-count">' + this.browseTotalCount + ' items</span>';
        html += '  </div>';
        html += '</div>';

        html += '<div class="moonfin-genres-toolbar">';
        html += '  <button class="moonfin-genres-toolbar-btn" data-action="sort">';
        html += '    <span class="material-icons">sort</span>';
        html += '    <span>' + (currentSort ? currentSort.label : 'Sort') + '</span>';
        html += '  </button>';
        html += '  <button class="moonfin-genres-toolbar-btn" data-action="filter">';
        html += '    <span class="material-icons">filter_list</span>';
        html += '    <span>' + (currentFilter ? currentFilter.label : 'Filter') + '</span>';
        html += '  </button>';

        html += '  <div class="moonfin-genres-letter-nav">';
        for (var i = 0; i < this.LETTERS.length; i++) {
            var letter = this.LETTERS[i];
            var activeClass = this.startLetter === letter ? ' active' : '';
            html += '<button class="moonfin-genres-letter-btn' + activeClass + '" data-letter="' + letter + '">' + letter + '</button>';
        }
        html += '  </div>';
        html += '</div>';

        if (this.browseLoading && this.browseItems.length === 0) {
            html += '<div class="moonfin-genres-loading"><div class="moonfin-genres-spinner"></div></div>';
        } else if (this.browseItems.length === 0) {
            html += '<div class="moonfin-genres-empty">No items found</div>';
        } else {
            html += '<div class="moonfin-genres-browse-grid">';
            for (var j = 0; j < this.browseItems.length; j++) {
                var item = this.browseItems[j];
                var posterUrl = API.getPrimaryImageUrl(item, { maxWidth: 300 });
                var year = item.ProductionYear || '';
                var rating = item.CommunityRating ? item.CommunityRating.toFixed(1) : '';
                var officialRating = item.OfficialRating || '';
                var typeLabel = item.Type === 'Movie' ? 'MOVIE' : item.Type === 'Series' ? 'SERIES' : '';

                html += '<div class="moonfin-genre-item-card" data-item-id="' + item.Id + '">';
                html += '  <div class="moonfin-genre-item-poster">';
                if (posterUrl) {
                    html += '    <img src="' + posterUrl + '" alt="' + (item.Name || '').replace(/"/g, '&quot;') + '" loading="lazy">';
                } else {
                    html += '    <div class="moonfin-genre-item-no-poster"><span class="material-icons">movie</span></div>';
                }
                if (typeLabel) {
                    html += '    <span class="moonfin-genre-item-type-badge ' + (item.Type === 'Movie' ? 'movie' : 'series') + '">' + typeLabel + '</span>';
                }
                html += '  </div>';
                html += '  <div class="moonfin-genre-item-info">';
                html += '    <div class="moonfin-genre-item-name">' + (item.Name || 'Unknown') + '</div>';
                html += '    <div class="moonfin-genre-item-meta">';
                if (year) html += '<span>' + year + '</span>';
                if (officialRating) html += '<span>' + officialRating + '</span>';
                if (rating) html += '<span>&#9733; ' + rating + '</span>';
                html += '    </div>';
                html += '  </div>';
                html += '</div>';
            }

            if (this.browseItems.length < this.browseTotalCount) {
                html += '<div class="moonfin-genres-load-more" data-action="load-more">';
                html += '  <button class="moonfin-genres-toolbar-btn">Load More</button>';
                html += '</div>';
            }
            html += '</div>';
        }

        this.container.innerHTML = html;
        this.bindBrowseEvents();
    },

    bindBrowseEvents: function() {
        var self = this;

        var itemCards = this.container.querySelectorAll('.moonfin-genre-item-card');
        for (var i = 0; i < itemCards.length; i++) {
            itemCards[i].addEventListener('click', function() {
                var itemId = this.dataset.itemId;
                if (itemId) {
                    if (typeof Details !== 'undefined' && Storage.get('detailsPageEnabled')) {
                        Details.showDetails(itemId);
                    } else {
                        API.navigateToItem(itemId);
                        self.isVisible = false;
                        self.container.classList.remove('visible');
                        document.body.style.overflow = '';
                    }
                }
            });
        }

        var sortBtn = this.container.querySelector('[data-action="sort"]');
        if (sortBtn) {
            sortBtn.addEventListener('click', function() { self.showSortMenu(); });
        }

        var filterBtn = this.container.querySelector('[data-action="filter"]');
        if (filterBtn) {
            filterBtn.addEventListener('click', function() { self.showFilterMenu(); });
        }

        var letterBtns = this.container.querySelectorAll('.moonfin-genres-letter-btn');
        for (var j = 0; j < letterBtns.length; j++) {
            letterBtns[j].addEventListener('click', function() {
                var letter = this.dataset.letter;
                self.startLetter = self.startLetter === letter ? null : letter;
                self.loadBrowseItems(true);
            });
        }

        var loadMoreBtn = this.container.querySelector('[data-action="load-more"]');
        if (loadMoreBtn) {
            loadMoreBtn.addEventListener('click', function() {
                self.browseStartIndex = self.browseItems.length;
                self.loadBrowseItems(false);
            });
        }

        var browseGrid = this.container.querySelector('.moonfin-genres-browse-grid');
        if (browseGrid) {
            this._scrollHandler = function() {
                if (self.browseLoading) return;
                if (self.browseItems.length >= self.browseTotalCount) return;

                var scrollTop = self.container.scrollTop;
                var scrollHeight = self.container.scrollHeight;
                var clientHeight = self.container.clientHeight;

                if (scrollTop + clientHeight >= scrollHeight - 400) {
                    self.browseStartIndex = self.browseItems.length;
                    self.loadBrowseItems(false);
                }
            };
            this.container.addEventListener('scroll', this._scrollHandler);
        }

        if (this._escHandler) {
            document.removeEventListener('keydown', this._escHandler);
        }
        this._escHandler = function(e) {
            if (e.key === 'Escape' && self.isVisible) {
                e.preventDefault();
                e.stopPropagation();
                self.hide();
            }
        };
        document.addEventListener('keydown', this._escHandler);
    },

    showSortMenu: function() {
        this.showDropdownMenu('Sort By', this.SORT_OPTIONS, this.sortBy + ',' + this.sortOrder, function(key) {
            var parts = key.split(',');
            this.sortBy = parts[0];
            this.sortOrder = parts[1];
            this.loadBrowseItems(true);
        }.bind(this));
    },

    showFilterMenu: function() {
        this.showDropdownMenu('Filter', this.FILTER_OPTIONS, this.filterType, function(key) {
            this.filterType = key;
            this.loadBrowseItems(true);
        }.bind(this));
    },

    showDropdownMenu: function(title, options, activeKey, onSelect) {
        var existing = document.querySelector('.moonfin-genres-dropdown');
        if (existing) existing.remove();

        var dropdown = document.createElement('div');
        dropdown.className = 'moonfin-genres-dropdown';

        var html = '<div class="moonfin-genres-dropdown-backdrop"></div>';
        html += '<div class="moonfin-genres-dropdown-content">';
        html += '<div class="moonfin-genres-dropdown-title">' + title + '</div>';
        for (var i = 0; i < options.length; i++) {
            var opt = options[i];
            var activeClass = opt.key === activeKey ? ' active' : '';
            html += '<button class="moonfin-genres-dropdown-option' + activeClass + '" data-key="' + opt.key + '">' + opt.label + '</button>';
        }
        html += '</div>';
        dropdown.innerHTML = html;

        document.body.appendChild(dropdown);

        dropdown.querySelector('.moonfin-genres-dropdown-backdrop').addEventListener('click', function() {
            dropdown.remove();
        });
        var optBtns = dropdown.querySelectorAll('.moonfin-genres-dropdown-option');
        for (var j = 0; j < optBtns.length; j++) {
            optBtns[j].addEventListener('click', function() {
                var key = this.dataset.key;
                dropdown.remove();
                onSelect(key);
            });
        }

        requestAnimationFrame(function() { dropdown.classList.add('visible'); });
    },

    destroy: function() {
        if (this._escHandler) {
            document.removeEventListener('keydown', this._escHandler);
        }
        if (this.container) {
            this.container.remove();
            this.container = null;
        }
        this.isVisible = false;
    }
};
