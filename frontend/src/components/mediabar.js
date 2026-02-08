const MediaBar = {
    container: null,
    initialized: false,
    items: [],
    currentIndex: 0,
    isPaused: false,
    autoAdvanceTimer: null,
    isVisible: true,

    async init() {
        const settings = Storage.getAll();
        if (!settings.mediaBarEnabled) {
            console.log('[Moonfin] Media bar is disabled');
            document.body.classList.remove('moonfin-mediabar-active');
            return;
        }

        if (this.initialized) return;

        console.log('[Moonfin] Initializing media bar...');

        try {
            await this.waitForApi();
        } catch (e) {
            console.error('[Moonfin] MediaBar: Failed to initialize -', e.message);
            document.body.classList.remove('moonfin-mediabar-active');
            return;
        }

        this.createMediaBar();

        await this.loadContent();

        if (this.items.length > 0 && Plugin.isHomePage()) {
            document.body.classList.add('moonfin-mediabar-active');
        } else {
            document.body.classList.remove('moonfin-mediabar-active');
        }

        this.setupEventListeners();

        if (settings.mediaBarAutoAdvance) {
            this.startAutoAdvance();
        }

        this.initialized = true;
        console.log('[Moonfin] Media bar initialized with', this.items.length, 'items');
    },

    waitForApi() {
        return new Promise((resolve, reject) => {
            let attempts = 0;
            const maxAttempts = 100;
            
            const check = () => {
                const api = API.getApiClient();
                if (api && api._currentUser && api._currentUser.Id) {
                    console.log('[Moonfin] MediaBar: API ready with authenticated user');
                    resolve();
                } else if (attempts >= maxAttempts) {
                    console.warn('[Moonfin] MediaBar: Timeout waiting for API');
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
        const existing = document.querySelector('.moonfin-mediabar');
        if (existing) {
            existing.remove();
        }

        const settings = Storage.getAll();
        const overlayColor = Storage.getColorRgba(settings.mediaBarOverlayColor, settings.mediaBarOverlayOpacity);

        this.container = document.createElement('div');
        this.container.className = 'moonfin-mediabar';
        this.container.innerHTML = `
            <div class="moonfin-mediabar-backdrop">
                <div class="moonfin-mediabar-backdrop-img moonfin-mediabar-backdrop-current"></div>
                <div class="moonfin-mediabar-backdrop-img moonfin-mediabar-backdrop-next"></div>
            </div>
            <div class="moonfin-mediabar-gradient"></div>
            <div class="moonfin-mediabar-content">
                <!-- Left: Info overlay -->
                <div class="moonfin-mediabar-info" style="background: ${overlayColor}">
                    <div class="moonfin-mediabar-metadata">
                        <span class="moonfin-mediabar-year"></span>
                        <span class="moonfin-mediabar-runtime"></span>
                    </div>
                    <div class="moonfin-mediabar-genres"></div>
                    <div class="moonfin-mediabar-ratings"></div>
                    <div class="moonfin-mediabar-overview"></div>
                </div>
                <!-- Right: Logo -->
                <div class="moonfin-mediabar-logo-container">
                    <img class="moonfin-mediabar-logo" src="" alt="">
                </div>
            </div>
            <!-- Navigation -->
            <div class="moonfin-mediabar-nav">
                <button class="moonfin-mediabar-nav-btn moonfin-mediabar-prev" style="background: ${overlayColor}">
                    <svg viewBox="0 0 24 24">
                        <path fill="currentColor" d="M15.41 7.41L14 6l-6 6 6 6 1.41-1.41L10.83 12z"/>
                    </svg>
                </button>
                <button class="moonfin-mediabar-nav-btn moonfin-mediabar-next" style="background: ${overlayColor}">
                    <svg viewBox="0 0 24 24">
                        <path fill="currentColor" d="M8.59 16.59L10 18l6-6-6-6-1.41 1.41L13.17 12z"/>
                    </svg>
                </button>
            </div>
            <!-- Dots indicator -->
            <div class="moonfin-mediabar-dots"></div>
            <!-- Play/Pause indicator -->
            <div class="moonfin-mediabar-playstate">
                <svg viewBox="0 0 24 24" class="moonfin-mediabar-play-icon">
                    <path fill="currentColor" d="M8 5v14l11-7z"/>
                </svg>
                <svg viewBox="0 0 24 24" class="moonfin-mediabar-pause-icon">
                    <path fill="currentColor" d="M6 19h4V5H6v14zm8-14v14h4V5h-4z"/>
                </svg>
            </div>
        `;

        // Insert into document.body so it persists across SPA navigation
        document.body.appendChild(this.container);
    },

    async loadContent() {
        const settings = Storage.getAll();
        
        this.items = await API.getRandomItems({
            contentType: settings.mediaBarContentType,
            limit: settings.mediaBarItemCount
        });

        if (this.items.length > 0) {
            this.updateDisplay();
            this.updateDots();
        } else {
            console.log('[Moonfin] No items found for media bar');
            this.container.classList.add('empty');
        }
    },

    updateDisplay() {
        const item = this.items[this.currentIndex];
        if (!item) return;

        const backdropUrl = API.getImageUrl(item, 'Backdrop', { maxWidth: 1920 });
        this.updateBackdrop(backdropUrl);

        const logoUrl = API.getImageUrl(item, 'Logo', { maxWidth: 500 });
        const logoContainer = this.container.querySelector('.moonfin-mediabar-logo-container');
        const logoImg = this.container.querySelector('.moonfin-mediabar-logo');
        
        if (logoUrl) {
            logoImg.src = logoUrl;
            logoImg.alt = item.Name;
            logoContainer.classList.remove('hidden');
        } else {
            logoContainer.classList.add('hidden');
        }

        const yearEl = this.container.querySelector('.moonfin-mediabar-year');
        const runtimeEl = this.container.querySelector('.moonfin-mediabar-runtime');
        const ratingsEl = this.container.querySelector('.moonfin-mediabar-ratings');
        const genresEl = this.container.querySelector('.moonfin-mediabar-genres');
        const overviewEl = this.container.querySelector('.moonfin-mediabar-overview');

        yearEl.textContent = item.ProductionYear || '';

        if (item.RunTimeTicks) {
            const minutes = Math.round(item.RunTimeTicks / 600000000);
            const hours = Math.floor(minutes / 60);
            const mins = minutes % 60;
            runtimeEl.textContent = hours > 0 ? `${hours}h ${mins}m` : `${mins}m`;
        } else {
            runtimeEl.textContent = '';
        }

        // Build ratings line
        var ratingParts = [];
        if (item.OfficialRating) {
            ratingParts.push(item.OfficialRating);
        }
        if (item.CommunityRating) {
            ratingParts.push('★ ' + item.CommunityRating.toFixed(1));
        }
        if (item.CriticRating) {
            ratingParts.push('🍅 ' + item.CriticRating + '%');
        }
        ratingsEl.textContent = ratingParts.join('  •  ');

        // Fetch and show MDBList ratings if enabled
        if (MdbList.isEnabled()) {
            var currentIdx = this.currentIndex;
            MdbList.fetchRatings(item).then(function(mdbRatings) {
                // Only update if still on the same slide
                if (MediaBar.currentIndex !== currentIdx) return;
                if (mdbRatings && mdbRatings.length > 0) {
                    var mdbHtml = MdbList.buildRatingsHtml(mdbRatings, 'compact');
                    if (mdbHtml) {
                        ratingsEl.innerHTML = mdbHtml;
                    }
                }
            });
        }

        if (item.Genres && item.Genres.length > 0) {
            genresEl.textContent = item.Genres.slice(0, 3).join(' • ');
        } else {
            genresEl.textContent = '';
        }

        if (item.Overview) {
            overviewEl.textContent = item.Overview;
        } else {
            overviewEl.textContent = '';
        }

        this.updateActiveDot();
    },

    updateBackdrop(url) {
        const current = this.container.querySelector('.moonfin-mediabar-backdrop-current');
        const next = this.container.querySelector('.moonfin-mediabar-backdrop-next');

        if (!url) {
            current.style.backgroundImage = '';
            return;
        }

        // Cancel any pending crossfade
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
        const dotsContainer = this.container.querySelector('.moonfin-mediabar-dots');
        const settings = Storage.getAll();
        const overlayColor = Storage.getColorRgba(settings.mediaBarOverlayColor, settings.mediaBarOverlayOpacity);

        dotsContainer.innerHTML = this.items.map((_, index) => `
            <button class="moonfin-mediabar-dot ${index === this.currentIndex ? 'active' : ''}" 
                    data-index="${index}"
                    style="background: ${index === this.currentIndex ? '#fff' : overlayColor}">
            </button>
        `).join('');
    },

    updateActiveDot() {
        const dots = this.container.querySelectorAll('.moonfin-mediabar-dot');
        const settings = Storage.getAll();
        const overlayColor = Storage.getColorRgba(settings.mediaBarOverlayColor, settings.mediaBarOverlayOpacity);

        dots.forEach((dot, index) => {
            dot.classList.toggle('active', index === this.currentIndex);
            dot.style.background = index === this.currentIndex ? '#fff' : overlayColor;
        });
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
        const settings = Storage.getAll();
        if (!settings.mediaBarAutoAdvance) return;

        this.autoAdvanceTimer = setInterval(() => {
            if (!this.isPaused && this.isVisible) {
                this.nextSlide();
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
            console.log('[Moonfin] MediaBar: Re-inserting container into DOM');
            document.body.appendChild(this.container);
        }
    },

    setupEventListeners() {
        this.container.querySelector('.moonfin-mediabar-prev')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this.prevSlide();
        });

        this.container.querySelector('.moonfin-mediabar-next')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this.nextSlide();
        });

        this.container.querySelector('.moonfin-mediabar-dots')?.addEventListener('click', (e) => {
            e.stopPropagation();
            const dot = e.target.closest('.moonfin-mediabar-dot');
            if (dot) {
                this.goToSlide(parseInt(dot.dataset.index, 10));
            }
        });

        this.container.addEventListener('click', (e) => {
            if (e.target.closest('.moonfin-mediabar-nav-btn, .moonfin-mediabar-dots, .moonfin-mediabar-playstate')) {
                return;
            }
            const item = this.items[this.currentIndex];
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
        var self = this;

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
            if (dx > 10 || dy > 10) {
                touchMoved = true;
            }
            if (dx > dy && dx > 10) {
                e.preventDefault();
            }
        }, { passive: false });

        this.container.addEventListener('touchend', function(e) {
            if (!touchMoved) {
                touchStartX = 0;
                return;
            }
            var touch = e.changedTouches[0];
            var dx = touch.clientX - touchStartX;
            var minSwipe = 50;
            if (Math.abs(dx) >= minSwipe) {
                if (dx < 0) {
                    self.nextSlide();
                } else {
                    self.prevSlide();
                }
            }
            touchStartX = 0;
            touchMoved = false;
        }, { passive: true });

        this.container.addEventListener('keydown', (e) => {
            switch (e.key) {
                case 'ArrowLeft':
                    this.prevSlide();
                    e.preventDefault();
                    break;
                case 'ArrowRight':
                    this.nextSlide();
                    e.preventDefault();
                    break;
                case ' ':
                    this.togglePause();
                    e.preventDefault();
                    break;
                case 'Enter':
                    const item = this.items[this.currentIndex];
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

        this.container.addEventListener('mouseenter', () => {
            this.container.classList.add('focused');
        });

        this.container.addEventListener('mouseleave', () => {
            this.container.classList.remove('focused');
        });

        document.addEventListener('visibilitychange', () => {
            this.isVisible = !document.hidden;
        });

        window.addEventListener('moonfin-settings-changed', (e) => {
            this.applySettings(e.detail);
        });

        // Note: visibility toggling handled by Plugin.onPageChange()
    },

    applySettings(settings) {
        if (!this.container) return;

        if (!settings.mediaBarEnabled) {
            this.hide();
            return;
        } else {
            this.show();
        }

        const overlayColor = Storage.getColorRgba(settings.mediaBarOverlayColor, settings.mediaBarOverlayOpacity);

        const infoBox = this.container.querySelector('.moonfin-mediabar-info');
        if (infoBox) {
            infoBox.style.background = overlayColor;
        }

        this.container.querySelectorAll('.moonfin-mediabar-nav-btn').forEach(btn => {
            btn.style.background = overlayColor;
        });

        this.updateDots();

        this.resetAutoAdvance();

        if (this._lastContentType !== settings.mediaBarContentType || 
            this._lastItemCount !== settings.mediaBarItemCount) {
            this._lastContentType = settings.mediaBarContentType;
            this._lastItemCount = settings.mediaBarItemCount;
            this.loadContent();
        }
    },

    isHomePage() {
        return Plugin.isHomePage();
    },

    show() {
        if (this.container) {
            this.container.classList.remove('disabled');
            if (this.isHomePage()) {
                document.body.classList.add('moonfin-mediabar-active');
            }
        }
    },

    hide() {
        if (this.container) {
            this.container.classList.add('disabled');
            document.body.classList.remove('moonfin-mediabar-active');
        }
    },

    async refresh() {
        this.currentIndex = 0;
        await this.loadContent();
    },

    destroy() {
        this.stopAutoAdvance();
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
