var MediaBar = {
    container: null,
    initialized: false,
    items: [],
    currentIndex: 0,
    isPaused: false,
    autoAdvanceTimer: null,
    isVisible: true,

    _trailerState: 'idle',
    _trailerPlayer: null,
    _trailerRevealTimer: null,
    _trailerVideoId: null,
    _sponsorSegments: [],
    _trailerRevealMs: 4000,
    _ytApiReady: false,
    _ytApiLoading: false,

    async init() {
        var settings = Storage.getAll();
        if (!settings.mediaBarEnabled) {
            document.body.classList.remove('moonfin-mediabar-active');
            return;
        }

        if (this.initialized) return;

        this.createMediaBar();
        this.container.classList.add('loading');

        if (Plugin.isHomePage()) {
            document.body.classList.add('moonfin-mediabar-active');
        }

        this.setupEventListeners();
        this.initialized = true;

        this._loadContentAsync(settings);
    },

    _loadContentAsync(settings) {
        var self = this;
        this.waitForApi().then(function() {
            return self.loadContent();
        }).then(function() {
            if (self.items.length > 0) {
                self.container.classList.remove('loading');
                if (settings.mediaBarAutoAdvance) {
                    self.startAutoAdvance();
                }
            } else {
                document.body.classList.remove('moonfin-mediabar-active');
                self.container.classList.add('empty');
            }
        }).catch(function(e) {
            console.error('[Moonfin] MediaBar: Failed to load content -', e.message);
            document.body.classList.remove('moonfin-mediabar-active');
            if (self.container) self.container.classList.add('empty');
        });
    },

    waitForApi() {
        return new Promise(function(resolve, reject) {
            var attempts = 0;
            var maxAttempts = 50;
            
            var check = function() {
                var api = API.getApiClient();
                if (api) {
                    try {
                        var userId = api.getCurrentUserId();
                        if (userId) {
                            resolve();
                            return;
                        }
                    } catch (e) {
                        // Not authenticated yet
                    }
                }
                
                if (attempts >= maxAttempts) {
                    reject(new Error('API timeout'));
                } else {
                    attempts++;
                    setTimeout(check, 100);
                }
            };
            check();
        });
    },

    createMediaBar() {
        var existing = document.querySelector('.moonfin-mediabar');
        if (existing) {
            existing.remove();
        }

        var settings = Storage.getAll();
        var overlayColor = Storage.getColorRgba(settings.mediaBarOverlayColor, settings.mediaBarOverlayOpacity);

        this.container = document.createElement('div');
        this.container.className = 'moonfin-mediabar';
        this.container.innerHTML =
            '<div class="moonfin-mediabar-backdrop">' +
                '<div class="moonfin-mediabar-backdrop-img moonfin-mediabar-backdrop-current"></div>' +
                '<div class="moonfin-mediabar-backdrop-img moonfin-mediabar-backdrop-next"></div>' +
            '</div>' +
            '<div class="moonfin-mediabar-trailer-container"></div>' +
            '<div class="moonfin-mediabar-gradient"></div>' +
            '<div class="moonfin-mediabar-content">' +
                '<div class="moonfin-mediabar-logo-container">' +
                    '<img class="moonfin-mediabar-logo" src="" alt="">' +
                '</div>' +
                '<div class="moonfin-mediabar-info" style="background: ' + overlayColor + '">' +
                    '<div class="moonfin-mediabar-metadata">' +
                        '<span class="moonfin-mediabar-year"></span>' +
                        '<span class="moonfin-mediabar-rating-badge"></span>' +
                        '<span class="moonfin-mediabar-runtime"></span>' +
                        '<span class="moonfin-mediabar-genres"></span>' +
                    '</div>' +
                    '<div class="moonfin-mediabar-ratings"></div>' +
                    '<div class="moonfin-mediabar-overview"></div>' +
                '</div>' +
            '</div>' +
            '<div class="moonfin-mediabar-nav">' +
                '<button class="moonfin-mediabar-nav-btn moonfin-mediabar-prev" style="background: ' + overlayColor + '">' +
                    '<svg viewBox="0 0 24 24"><path fill="currentColor" d="M15.41 7.41L14 6l-6 6 6 6 1.41-1.41L10.83 12z"/></svg>' +
                '</button>' +
                '<button class="moonfin-mediabar-nav-btn moonfin-mediabar-next" style="background: ' + overlayColor + '">' +
                    '<svg viewBox="0 0 24 24"><path fill="currentColor" d="M8.59 16.59L10 18l6-6-6-6-1.41 1.41L13.17 12z"/></svg>' +
                '</button>' +
            '</div>' +
            '<div class="moonfin-mediabar-dots-wrap" style="background: ' + overlayColor + '">' +
                '<div class="moonfin-mediabar-dots"></div>' +
            '</div>';

        document.body.appendChild(this.container);
    },

    async loadContent() {
        var settings = Storage.getAll();
        
        this.items = await API.getRandomItems({
            contentType: settings.mediaBarContentType,
            limit: settings.mediaBarItemCount
        });

        if (this.items.length > 0) {
            this.updateDisplay();
            this.updateDots();
        } else {
            this.container.classList.add('empty');
        }
    },

    updateDisplay() {
        var item = this.items[this.currentIndex];
        if (!item) return;

        this.stopTrailer();

        var backdropUrl = API.getImageUrl(item, 'Backdrop', { maxWidth: 1920 });
        this.updateBackdrop(backdropUrl);

        var logoUrl = API.getImageUrl(item, 'Logo', { maxWidth: 500 });
        var logoContainer = this.container.querySelector('.moonfin-mediabar-logo-container');
        var logoImg = this.container.querySelector('.moonfin-mediabar-logo');
        
        if (logoUrl) {
            logoImg.src = logoUrl;
            logoImg.alt = item.Name;
            logoContainer.classList.remove('hidden');
        } else {
            logoContainer.classList.add('hidden');
        }

        var yearEl = this.container.querySelector('.moonfin-mediabar-year');
        var ratingBadge = this.container.querySelector('.moonfin-mediabar-rating-badge');
        var runtimeEl = this.container.querySelector('.moonfin-mediabar-runtime');
        var genresEl = this.container.querySelector('.moonfin-mediabar-genres');
        var ratingsEl = this.container.querySelector('.moonfin-mediabar-ratings');
        var overviewEl = this.container.querySelector('.moonfin-mediabar-overview');

        yearEl.textContent = item.ProductionYear || '';

        if (item.OfficialRating) {
            ratingBadge.textContent = item.OfficialRating;
            ratingBadge.classList.remove('hidden');
        } else {
            ratingBadge.textContent = '';
            ratingBadge.classList.add('hidden');
        }

        if (item.RunTimeTicks) {
            var minutes = Math.round(item.RunTimeTicks / 600000000);
            var hours = Math.floor(minutes / 60);
            var mins = minutes % 60;
            runtimeEl.textContent = hours > 0 ? hours + 'h ' + mins + 'm' : mins + 'm';
        } else {
            runtimeEl.textContent = '';
        }

        if (item.Genres && item.Genres.length > 0) {
            genresEl.textContent = item.Genres.slice(0, 3).join(' \u2022 ');
        } else {
            genresEl.textContent = '';
        }

        var ratingParts = [];
        if (item.CommunityRating) {
            ratingParts.push('\u2605 ' + item.CommunityRating.toFixed(1));
        }
        if (item.CriticRating) {
            ratingParts.push('\uD83C\uDF45 ' + item.CriticRating + '%');
        }
        ratingsEl.textContent = ratingParts.join('  \u2022  ');

        if (MdbList.isEnabled()) {
            var currentIdx = this.currentIndex;
            MdbList.fetchRatings(item).then(function(mdbRatings) {
                if (MediaBar.currentIndex !== currentIdx) return;
                if (mdbRatings && mdbRatings.length > 0) {
                    var mdbHtml = MdbList.buildRatingsHtml(mdbRatings, 'compact');
                    if (mdbHtml) {
                        ratingsEl.innerHTML = mdbHtml;
                    }
                }
            });
        }

        if (item.Overview) {
            var tmp = document.createElement('div');
            tmp.innerHTML = item.Overview;
            overviewEl.textContent = tmp.textContent || tmp.innerText || '';
        } else {
            overviewEl.textContent = '';
        }

        this.updateActiveDot();

        var settings = Storage.getAll();
        var videoId = this.extractYouTubeId(item);
        if (settings.mediaBarTrailerPreview && videoId) {
            this.startTrailerPreview(videoId);
        }
    },

    extractYouTubeId(item) {
        var trailers = item.RemoteTrailers;
        if (!trailers || trailers.length === 0) return null;

        for (var i = 0; i < trailers.length; i++) {
            var url = trailers[i].Url || trailers[i].url || '';
            var match = url.match(/(?:youtube\.com\/(?:watch\?v=|embed\/)|youtu\.be\/)([a-zA-Z0-9_-]{11})/);
            if (match) return match[1];
        }
        return null;
    },

    startTrailerPreview(videoId) {
        var self = this;
        this._trailerState = 'resolving';
        this._trailerVideoId = videoId;

        this._ensureYTApi(function() {
            if (self._trailerState !== 'resolving' || self._trailerVideoId !== videoId) return;
            self.fetchSponsorSegments(videoId).then(function(segments) {
                self._sponsorSegments = segments;
                self._loadYTPlayer(videoId);
            }).catch(function() {
                self._sponsorSegments = [];
                self._loadYTPlayer(videoId);
            });
        });
    },

    _ensureYTApi(callback) {
        if (this._ytApiReady && window.YT && window.YT.Player) {
            callback();
            return;
        }
        var self = this;
        if (!this._ytApiLoading) {
            this._ytApiLoading = true;
            var tag = document.createElement('script');
            tag.src = 'https://www.youtube.com/iframe_api';
            document.head.appendChild(tag);
        }
        var checkInterval = setInterval(function() {
            if (window.YT && window.YT.Player) {
                clearInterval(checkInterval);
                self._ytApiReady = true;
                self._ytApiLoading = false;
                callback();
            }
        }, 100);
        setTimeout(function() { clearInterval(checkInterval); }, 10000);
    },

    _loadYTPlayer(videoId) {
        if (this._trailerState !== 'resolving') return;

        var self = this;
        var startTime = this.getTrailerStartTime(this._sponsorSegments);
        var trailerContainer = this.container.querySelector('.moonfin-mediabar-trailer-container');

        if (this._trailerPlayer) {
            try { this._trailerPlayer.destroy(); } catch(e) {}
            this._trailerPlayer = null;
        }

        var playerDiv = document.createElement('div');
        playerDiv.id = 'moonfin-yt-player-' + Date.now();
        playerDiv.className = 'moonfin-mediabar-trailer-iframe';
        trailerContainer.innerHTML = '';
        trailerContainer.appendChild(playerDiv);

        this._trailerState = 'playing';
        this.stopAutoAdvance();

        try {
            this._trailerPlayer = new YT.Player(playerDiv.id, {
                videoId: videoId,
                playerVars: {
                    autoplay: 1,
                    mute: 1,
                    controls: 0,
                    start: Math.floor(startTime),
                    rel: 0,
                    modestbranding: 1,
                    playsinline: 1,
                    showinfo: 0,
                    iv_load_policy: 3,
                    disablekb: 1,
                    fs: 0,
                    origin: window.location.origin
                },
                events: {
                    onReady: function(event) {
                        event.target.mute();
                        event.target.playVideo();
                        self._trailerRevealTimer = setTimeout(function() {
                            if (self._trailerState === 'playing') {
                                var iframe = trailerContainer.querySelector('iframe');
                                if (iframe) iframe.classList.add('visible');
                                self.container.classList.add('trailer-active');
                            }
                        }, self._trailerRevealMs);
                    },
                    onStateChange: function(event) {
                        if (event.data === 0) {
                            self.stopTrailer();
                        }
                    },
                    onError: function(event) {
                        console.warn('[Moonfin] MediaBar: YouTube player error:', event.data);
                        self._trailerState = 'unavailable';
                        self.stopTrailer();
                    }
                }
            });
        } catch(e) {
            console.warn('[Moonfin] MediaBar: Failed to create YouTube player:', e);
            this._trailerState = 'unavailable';
        }
    },

    fetchSponsorSegments(videoId) {
        return new Promise(function(resolve) {
            var url = 'https://sponsor.ajay.app/api/skipSegments?videoID=' + videoId +
                      '&categories=["sponsor","selfpromo","intro","outro","interaction","music_offtopic"]';
            
            fetch(url).then(function(resp) {
                if (!resp.ok) { resolve([]); return; }
                return resp.json();
            }).then(function(data) {
                if (!Array.isArray(data)) { resolve([]); return; }
                var segments = [];
                for (var i = 0; i < data.length; i++) {
                    if (data[i].segment && data[i].segment.length === 2) {
                        segments.push({ start: data[i].segment[0], end: data[i].segment[1] });
                    }
                }
                resolve(segments);
            }).catch(function() {
                resolve([]);
            });
        });
    },

    getTrailerStartTime(segments) {
        var startTime = 0;
        if (!segments || segments.length === 0) return startTime;

        var sorted = segments.slice().sort(function(a, b) { return a.start - b.start; });
        for (var i = 0; i < sorted.length; i++) {
            if (sorted[i].start <= startTime + 1) {
                startTime = Math.max(startTime, sorted[i].end);
            }
        }
        return Math.max(startTime, 5);
    },

    stopTrailer() {
        if (this._trailerRevealTimer) {
            clearTimeout(this._trailerRevealTimer);
            this._trailerRevealTimer = null;
        }

        if (this.container) this.container.classList.remove('trailer-active');

        if (this._trailerPlayer) {
            try { this._trailerPlayer.destroy(); } catch(e) {}
            this._trailerPlayer = null;
        }

        var trailerContainer = this.container ? this.container.querySelector('.moonfin-mediabar-trailer-container') : null;
        if (trailerContainer) trailerContainer.innerHTML = '';

        this._trailerState = 'idle';
        this._trailerVideoId = null;
        this._sponsorSegments = [];

        if (!this.isPaused) {
            var settings = Storage.getAll();
            if (settings.mediaBarAutoAdvance && !this.autoAdvanceTimer) {
                this.startAutoAdvance();
            }
        }
    },

    updateBackdrop(url) {
        var current = this.container.querySelector('.moonfin-mediabar-backdrop-current');
        var next = this.container.querySelector('.moonfin-mediabar-backdrop-next');

        if (!url) {
            current.style.backgroundImage = '';
            return;
        }

        if (this._crossfadeTimer) {
            clearTimeout(this._crossfadeTimer);
            this._crossfadeTimer = null;
        }

        var img = new Image();
        var self = this;
        var doSwap = function() {
            next.style.transition = 'none';
            next.classList.remove('active');
            next.style.backgroundImage = "url('" + url + "')";

            void next.offsetWidth;
            next.style.transition = '';
            next.classList.add('active');

            self._crossfadeTimer = setTimeout(function() {
                current.style.backgroundImage = "url('" + url + "')";
                next.style.transition = 'none';
                next.classList.remove('active');
                void next.offsetWidth;
                next.style.transition = '';
                self._crossfadeTimer = null;
            }, 500);
        };

        img.onload = doSwap;
        img.onerror = doSwap;
        setTimeout(function() {
            if (!img.complete) doSwap();
        }, 300);
        img.src = url;

        this.preloadAdjacent();
    },

    preloadAdjacent() {
        if (!this.items || this.items.length < 2) return;
        var nextIdx = (this.currentIndex + 1) % this.items.length;
        var prevIdx = (this.currentIndex - 1 + this.items.length) % this.items.length;
        var nextUrl = API.getImageUrl(this.items[nextIdx], 'Backdrop', { maxWidth: 1920 });
        var prevUrl = API.getImageUrl(this.items[prevIdx], 'Backdrop', { maxWidth: 1920 });
        if (nextUrl) { var i1 = new Image(); i1.src = nextUrl; }
        if (prevUrl) { var i2 = new Image(); i2.src = prevUrl; }
    },

    updateDots() {
        var dotsContainer = this.container.querySelector('.moonfin-mediabar-dots');
        var html = '';
        for (var i = 0; i < this.items.length; i++) {
            html += '<button class="moonfin-mediabar-dot' + (i === this.currentIndex ? ' active' : '') + '" data-index="' + i + '"></button>';
        }
        dotsContainer.innerHTML = html;
    },

    updateActiveDot() {
        var dots = this.container.querySelectorAll('.moonfin-mediabar-dot');
        for (var i = 0; i < dots.length; i++) {
            dots[i].classList.toggle('active', i === this.currentIndex);
        }
    },

    nextSlide() {
        this.currentIndex = (this.currentIndex + 1) % this.items.length;
        this.updateDisplay();
        this.resetAutoAdvance();
    },

    prevSlide() {
        this.currentIndex = (this.currentIndex - 1 + this.items.length) % this.items.length;
        this.updateDisplay();
        this.resetAutoAdvance();
    },

    goToSlide(index) {
        if (index >= 0 && index < this.items.length) {
            this.currentIndex = index;
            this.updateDisplay();
            this.resetAutoAdvance();
        }
    },

    togglePause() {
        this.isPaused = !this.isPaused;
        this.container.classList.toggle('paused', this.isPaused);

        if (this.isPaused) {
            this.stopAutoAdvance();
        } else {
            this.startAutoAdvance();
        }
    },

    startAutoAdvance() {
        var self = this;
        var settings = Storage.getAll();
        if (!settings.mediaBarAutoAdvance) return;

        this.autoAdvanceTimer = setInterval(function() {
            if (!self.isPaused && self.isVisible && self._trailerState === 'idle') {
                self.nextSlide();
            }
        }, settings.mediaBarIntervalMs);
    },

    stopAutoAdvance() {
        if (this.autoAdvanceTimer) {
            clearInterval(this.autoAdvanceTimer);
            this.autoAdvanceTimer = null;
        }
    },

    resetAutoAdvance() {
        this.stopAutoAdvance();
        if (!this.isPaused) {
            this.startAutoAdvance();
        }
    },

    ensureInDOM() {
        if (this.container && !document.body.contains(this.container)) {

            document.body.appendChild(this.container);
        }
    },

    setupEventListeners() {
        var self = this;

        this.container.querySelector('.moonfin-mediabar-prev').addEventListener('click', function(e) {
            e.stopPropagation();
            self.prevSlide();
        });

        this.container.querySelector('.moonfin-mediabar-next').addEventListener('click', function(e) {
            e.stopPropagation();
            self.nextSlide();
        });

        this.container.querySelector('.moonfin-mediabar-dots').addEventListener('click', function(e) {
            e.stopPropagation();
            var dot = e.target.closest('.moonfin-mediabar-dot');
            if (dot) {
                self.goToSlide(parseInt(dot.dataset.index, 10));
            }
        });

        this.container.addEventListener('click', function(e) {
            if (e.target.closest('.moonfin-mediabar-nav-btn, .moonfin-mediabar-dots, .moonfin-mediabar-dots-wrap')) {
                return;
            }
            var item = self.items[self.currentIndex];
            if (item) {
                if (Storage.get('detailsPageEnabled')) {
                    Details.showDetails(item.Id, item.Type);
                } else {
                    API.navigateToItem(item.Id);
                }
            }
        });

        var touchStartX = 0;
        var touchStartY = 0;
        var touchMoved = false;

        this.container.addEventListener('touchstart', function(e) {
            var touch = e.touches[0];
            touchStartX = touch.clientX;
            touchStartY = touch.clientY;
            touchMoved = false;
        }, { passive: true });

        this.container.addEventListener('touchmove', function(e) {
            if (!touchStartX) return;
            var dx = Math.abs(e.touches[0].clientX - touchStartX);
            var dy = Math.abs(e.touches[0].clientY - touchStartY);
            if (dx > 10 || dy > 10) touchMoved = true;
            if (dx > dy && dx > 10) e.preventDefault();
        }, { passive: false });

        this.container.addEventListener('touchend', function(e) {
            if (!touchMoved) { touchStartX = 0; return; }
            var dx = e.changedTouches[0].clientX - touchStartX;
            if (Math.abs(dx) >= 50) {
                if (dx < 0) self.nextSlide();
                else self.prevSlide();
            }
            touchStartX = 0;
            touchMoved = false;
        }, { passive: true });

        this.container.addEventListener('keydown', function(e) {
            switch (e.key) {
                case 'ArrowLeft': self.prevSlide(); e.preventDefault(); break;
                case 'ArrowRight': self.nextSlide(); e.preventDefault(); break;
                case ' ': self.togglePause(); e.preventDefault(); break;
                case 'Enter':
                    var item = self.items[self.currentIndex];
                    if (item) {
                        if (Storage.get('detailsPageEnabled')) {
                            Details.showDetails(item.Id, item.Type);
                        } else {
                            API.navigateToItem(item.Id);
                        }
                    }
                    e.preventDefault();
                    break;
            }
        });

        this.container.addEventListener('mouseenter', function() {
            self.container.classList.add('focused');
        });

        this.container.addEventListener('mouseleave', function() {
            self.container.classList.remove('focused');
        });

        document.addEventListener('visibilitychange', function() {
            self.isVisible = !document.hidden;
            if (document.hidden) {
                self.stopTrailer();
            }
        });

        window.addEventListener('moonfin-settings-changed', function(e) {
            self.applySettings(e.detail);
        });
    },

    applySettings(settings) {
        if (!this.container) return;

        if (!settings.mediaBarEnabled) {
            this.hide();
            return;
        } else {
            this.show();
        }

        var overlayColor = Storage.getColorRgba(settings.mediaBarOverlayColor, settings.mediaBarOverlayOpacity);

        var infoBox = this.container.querySelector('.moonfin-mediabar-info');
        if (infoBox) infoBox.style.background = overlayColor;

        this.container.querySelectorAll('.moonfin-mediabar-nav-btn').forEach(function(btn) {
            btn.style.background = overlayColor;
        });

        var dotsWrap = this.container.querySelector('.moonfin-mediabar-dots-wrap');
        if (dotsWrap) dotsWrap.style.background = overlayColor;

        this.updateDots();
        this.resetAutoAdvance();

        if (!settings.mediaBarTrailerPreview) {
            this.stopTrailer();
        }

        if (this._lastContentType !== settings.mediaBarContentType || 
            this._lastItemCount !== settings.mediaBarItemCount) {
            this._lastContentType = settings.mediaBarContentType;
            this._lastItemCount = settings.mediaBarItemCount;
            this.loadContent();
        }
    },

    show() {
        if (this.container) {
            this.container.classList.remove('disabled');
            if (Plugin.isHomePage()) {
                document.body.classList.add('moonfin-mediabar-active');
            }
        }
    },

    hide() {
        if (this.container) {
            this.container.classList.add('disabled');
            document.body.classList.remove('moonfin-mediabar-active');
            this.stopTrailer();
        }
    },

    async refresh() {
        this.currentIndex = 0;
        await this.loadContent();
    },

    destroy() {
        this.stopAutoAdvance();
        this.stopTrailer();
        if (this.container) {
            this.container.remove();
            this.container = null;
        }
        document.body.classList.remove('moonfin-mediabar-active');
        this.initialized = false;
        this.items = [];
        this.currentIndex = 0;
    }
};
