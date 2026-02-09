var Library = {
    container: null,
    isVisible: false,
    libraryId: null,
    libraryName: '',
    collectionType: '',
    items: [],
    totalCount: 0,
    sortBy: 'SortName',
    sortOrder: 'Ascending',
    filterType: 'all',
    startLetter: null,
    loading: false,
    startIndex: 0,

    get SORT_OPTIONS() { return Genres.SORT_OPTIONS; },
    get FILTER_OPTIONS() { return Genres.FILTER_OPTIONS; },
    get LETTERS() { return Genres.LETTERS; },
    get BATCH_SIZE() { return Genres.BATCH_SIZE; },

    init: function() {
        this.createContainer();
    },

    createContainer: function() {
        var existing = document.querySelector('.moonfin-library-overlay');
        if (existing) existing.remove();

        this.container = document.createElement('div');
        this.container.className = 'moonfin-genres-overlay moonfin-library-overlay';
        document.body.appendChild(this.container);
    },

    show: function(libraryId, libraryName, collectionType) {
        if (!this.container) this.createContainer();

        this.libraryId = libraryId;
        this.libraryName = libraryName || 'Library';
        this.collectionType = (collectionType || '').toLowerCase();
        this.items = [];
        this.totalCount = 0;
        this.startIndex = 0;
        this.sortBy = 'SortName';
        this.sortOrder = 'Ascending';
        this.startLetter = null;
        this.loading = false;

        // Determine default filter based on collection type
        if (this.collectionType === 'movies') {
            this.filterType = 'Movie';
        } else if (this.collectionType === 'tvshows') {
            this.filterType = 'Series';
        } else {
            this.filterType = 'all';
        }

        this.isVisible = true;
        this.container.classList.add('visible');
        document.body.style.overflow = 'hidden';
        history.pushState({ moonfinLibrary: true }, '');

        this.loadItems(true);
    },

    hide: function() {
        this.isVisible = false;
        if (this.container) this.container.classList.remove('visible');
        document.body.style.overflow = '';
        try { history.back(); } catch(e) {}
    },

    close: function() {
        this.isVisible = false;
        if (this.container) this.container.classList.remove('visible');
        document.body.style.overflow = '';
    },

    async loadItems(isReset) {
        if (isReset) {
            this.startIndex = 0;
            this.items = [];
            this.loading = true;
            this.render();
        }

        var includeItemTypes;
        if (this.filterType === 'all') {
            if (this.collectionType === 'movies') {
                includeItemTypes = 'Movie';
            } else if (this.collectionType === 'tvshows') {
                includeItemTypes = 'Series';
            } else {
                includeItemTypes = 'Movie,Series';
            }
        } else {
            includeItemTypes = this.filterType;
        }

        try {
            var result = await API.getLibraryItems(this.libraryId, {
                startIndex: this.startIndex,
                limit: this.BATCH_SIZE,
                sortBy: this.sortBy,
                sortOrder: this.sortOrder,
                includeItemTypes: includeItemTypes,
                nameStartsWith: this.startLetter && this.startLetter !== '#' ? this.startLetter : null,
                nameLessThan: this.startLetter === '#' ? 'A' : null
            });

            this.totalCount = result.TotalRecordCount || 0;

            if (isReset) {
                this.items = result.Items || [];
            } else {
                this.items = this.items.concat(result.Items || []);
            }

            this.loading = false;
            this.render();
        } catch (e) {
            console.error('[Moonfin] Failed to load library items:', e);
            this.loading = false;
            this.render();
        }
    },

    render: function() {
        if (!this.container) return;
        var self = this;

        var currentSortKey = this.sortBy + ',' + this.sortOrder;
        var currentSort = this.SORT_OPTIONS.find(function(o) { return o.key === currentSortKey; });
        var currentFilter = this.FILTER_OPTIONS.find(function(o) { return o.key === self.filterType; });

        // Only show filter when collection type doesn't lock it
        var showFilter = this.collectionType !== 'movies' && this.collectionType !== 'tvshows';

        var html = '';

        html += '<div class="moonfin-genres-header">';
        html += '  <div class="moonfin-genres-title-section">';
        html += '    <h1 class="moonfin-genres-title">' + this.libraryName + '</h1>';
        html += '    <span class="moonfin-genres-count">' + this.totalCount + ' items</span>';
        html += '  </div>';
        html += '</div>';

        html += '<div class="moonfin-genres-toolbar">';
        html += '  <button class="moonfin-genres-toolbar-btn" data-action="sort">';
        html += '    <span class="material-icons">sort</span>';
        html += '    <span>' + (currentSort ? currentSort.label : 'Sort') + '</span>';
        html += '  </button>';

        if (showFilter) {
            html += '  <button class="moonfin-genres-toolbar-btn" data-action="filter">';
            html += '    <span class="material-icons">filter_list</span>';
            html += '    <span>' + (currentFilter ? currentFilter.label : 'Filter') + '</span>';
            html += '  </button>';
        }

        html += '  <div class="moonfin-genres-letter-nav">';
        for (var i = 0; i < this.LETTERS.length; i++) {
            var letter = this.LETTERS[i];
            var activeClass = this.startLetter === letter ? ' active' : '';
            html += '<button class="moonfin-genres-letter-btn' + activeClass + '" data-letter="' + letter + '">' + letter + '</button>';
        }
        html += '  </div>';
        html += '</div>';

        if (this.loading && this.items.length === 0) {
            html += '<div class="moonfin-genres-loading"><div class="moonfin-genres-spinner"></div></div>';
        } else if (this.items.length === 0) {
            html += '<div class="moonfin-genres-empty">No items found</div>';
        } else {
            html += '<div class="moonfin-genres-browse-grid">';
            for (var j = 0; j < this.items.length; j++) {
                var item = this.items[j];
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

            if (this.items.length < this.totalCount) {
                html += '<div class="moonfin-genres-load-more" data-action="load-more">';
                html += '  <button class="moonfin-genres-toolbar-btn">Load More</button>';
                html += '</div>';
            }
            html += '</div>';
        }

        this.container.innerHTML = html;
        this.bindEvents();
    },

    bindEvents: function() {
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
                        self.close();
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
                self.loadItems(true);
            });
        }

        var loadMoreBtn = this.container.querySelector('[data-action="load-more"]');
        if (loadMoreBtn) {
            loadMoreBtn.addEventListener('click', function() {
                self.startIndex = self.items.length;
                self.loadItems(false);
            });
        }

        // Infinite scroll
        var browseGrid = this.container.querySelector('.moonfin-genres-browse-grid');
        if (browseGrid) {
            this._scrollHandler = function() {
                if (self.loading) return;
                if (self.items.length >= self.totalCount) return;

                var scrollTop = self.container.scrollTop;
                var scrollHeight = self.container.scrollHeight;
                var clientHeight = self.container.clientHeight;

                if (scrollTop + clientHeight >= scrollHeight - 400) {
                    self.startIndex = self.items.length;
                    self.loadItems(false);
                }
            };
            this.container.addEventListener('scroll', this._scrollHandler);
        }

        // Escape key
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
        Genres.showDropdownMenu('Sort By', this.SORT_OPTIONS, this.sortBy + ',' + this.sortOrder, function(key) {
            var parts = key.split(',');
            this.sortBy = parts[0];
            this.sortOrder = parts[1];
            this.loadItems(true);
        }.bind(this));
    },

    showFilterMenu: function() {
        Genres.showDropdownMenu('Filter', this.FILTER_OPTIONS, this.filterType, function(key) {
            this.filterType = key;
            this.loadItems(true);
        }.bind(this));
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
