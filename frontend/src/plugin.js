const Plugin = {
    version: '1.3.1',
    name: 'Moonfin Web Plugin',
    initialized: false,

    isHomePage() {
        const hash = window.location.hash.toLowerCase();
        if (hash === '#/home' || hash === '#/home.html') return true;
        if (hash.startsWith('#/home?') || hash.startsWith('#/home.html?')) {
            // Exclude tab-based sub-pages (e.g. favorites, collections)
            return hash.indexOf('tab=') === -1;
        }
        return false;
    },

    isAdminPage() {
        const hash = window.location.hash.toLowerCase();

        // Whitelist of known user-facing routes.
        // Everything else (dashboard pages, plugin config, user management,
        // scheduled tasks, networking, etc.) is treated as admin so the
        // plugin stays out of the way and never blocks the admin panel.
        const userRoutes = [
            '#/home',
            '#/movies',
            '#/tvshows',
            '#/music',
            '#/livetv',
            '#/details',
            '#/search',
            '#/favorites',
            '#/list',
            '#/mypreferencesmenu',
            '#/mypreferencesdisplay',
            '#/mypreferenceshome',
            '#/mypreferencesplayback',
            '#/mypreferencessubtitles',
            '#/mypreferencescontrol',
            '#/mypreferencesquickconnect',
            '#/video'
        ];

        // Empty hash (root) is a user page
        if (hash === '' || hash === '#' || hash === '#/') {
            return false;
        }

        // Check if the current hash starts with any known user route
        for (const route of userRoutes) {
            if (hash === route || hash.startsWith(route + '.html') ||
                hash.startsWith(route + '?') || hash.startsWith(route + '/')) {
                return false;
            }
        }

        // Any page not in the user whitelist is treated as admin
        return true;
    },

    async init() {
        if (this.initialized) return;

        if (!this._listenersRegistered) {
            this.setupGlobalListeners();
            this._listenersRegistered = true;
        }

        if (this.isAdminPage()) {
            console.log('[Moonfin] Skipping initialization on admin page');
            return;
        }

        console.log(`[Moonfin] ${this.name} v${this.version} initializing...`);

        Device.detect();

        if (document.readyState === 'loading') {
            await new Promise(resolve => {
                document.addEventListener('DOMContentLoaded', resolve);
            });
        }

        this.loadStyles();
        this.applyDeviceClasses();

        Storage.initSync();

        try {
            var settings = Storage.getAll();

            if (settings.navbarEnabled) {
                if (settings.navbarPosition === 'left') {
                    await Sidebar.init();
                } else {
                    await Navbar.init();
                }
            }

            if (settings.mediaBarEnabled) {
                MediaBar.init();
            }

            Genres.init();
            Library.init();
            MdbList.init();
            await Jellyseerr.init();
            Details.init();
            this.initSeasonalEffects();

            if (Device.isTV()) {
                TVNavigation.init();
            }
        } catch (e) {
            console.error('[Moonfin] Error initializing components:', e);
        }

        this.initialized = true;
        console.log('[Moonfin] Plugin initialized successfully');
    },

    applyDeviceClasses() {
        const device = Device.getInfo();
        document.body.classList.toggle('moonfin-mobile', device.isMobile);
        document.body.classList.toggle('moonfin-desktop', device.isDesktop);
        document.body.classList.toggle('moonfin-tv', device.isTV);
        document.body.classList.toggle('moonfin-touch', device.hasTouch);
        document.body.dataset.moonfinDevice = device.type;
    },

    loadStyles() {
        if (document.querySelector('link[href*="moonfin"]') || 
            document.querySelector('style[data-moonfin]')) {
            return;
        }

        const cssUrl = this.getPluginUrl('plugin.css');
        if (cssUrl) {
            const link = document.createElement('link');
            link.rel = 'stylesheet';
            link.href = cssUrl;
            document.head.appendChild(link);
        }
    },

    getPluginUrl(filename) {
        const scripts = document.querySelectorAll('script[src*="moonfin"]');
        if (scripts.length > 0) {
            const scriptSrc = scripts[0].src;
            return scriptSrc.replace(/[^/]+$/, filename);
        }
        return null;
    },

    initSeasonalEffects() {
        const settings = Storage.getAll();
        this.applySeasonalEffect(settings.seasonalSurprise);

        window.addEventListener('moonfin-settings-changed', (e) => {
            this.applySeasonalEffect(e.detail.seasonalSurprise);
        });
        
        window.addEventListener('hashchange', () => {
            const settings = Storage.getAll();
            this.applySeasonalEffect(settings.seasonalSurprise);
        });
    },

    _seasonalState: null,

    applySeasonalEffect(effect) {
        if (this._seasonalState) {
            this._seasonalState.stop();
            this._seasonalState = null;
        }
        document.querySelectorAll('.moonfin-seasonal-effect').forEach(el => el.remove());

        if (this.isAdminPage()) return;
        if (!effect || effect === 'none') return;

        const container = document.createElement('div');
        container.className = 'moonfin-seasonal-effect';
        document.body.appendChild(container);

        const engine = this._createSeasonalEngine(container, effect);
        if (engine) {
            this._seasonalState = engine;
            engine.start();
        }
    },

    _sineTable: (() => {
        const t = new Float32Array(360);
        for (let i = 0; i < 360; i++) t[i] = Math.sin(i * Math.PI / 180);
        return t;
    })(),

    _createSeasonalEngine(container, effect) {
        const w = () => window.innerWidth;
        const h = () => window.innerHeight;
        const sin = this._sineTable;
        let raf = null;
        let running = false;
        let frame = 0;
        const els = [];

        function makeEl(emoji, size) {
            const el = document.createElement('div');
            el.className = 'moonfin-particle';
            el.textContent = emoji;
            el.style.fontSize = size + 'px';
            el.style.position = 'absolute';
            el.style.willChange = 'transform, opacity';
            container.appendChild(el);
            return el;
        }

        function removeEl(el) {
            el.remove();
        }

        function posEl(el, x, y, opacity, extra) {
            let t = `translate(${x}px, ${y}px)`;
            if (extra) t += ' ' + extra;
            el.style.transform = t;
            el.style.opacity = opacity;
        }

        const config = this._getSeasonConfig(effect);
        if (!config) return null;

        const state = {
            particles: [],
            specials: [],
            specialTimer: 0,
            specialInterval: config.specialInterval || 300
        };

        return {
            start() {
                running = true;
                config.init(state, w(), h(), makeEl);
                const tick = () => {
                    if (!running) return;
                    frame++;
                    config.update(state, w(), h(), frame, makeEl, removeEl, posEl, sin);
                    raf = requestAnimationFrame(tick);
                };
                raf = requestAnimationFrame(tick);
            },
            stop() {
                running = false;
                if (raf) cancelAnimationFrame(raf);
                state.particles.forEach(p => { if (p.el) p.el.remove(); });
                state.specials.forEach(s => { if (s.el) s.el.remove(); });
                state.particles = [];
                state.specials = [];
            }
        };
    },

    _getSeasonConfig(effect) {
        switch (effect) {
            case 'winter': return this._winterConfig();
            case 'spring': return this._springConfig();
            case 'summer': return this._summerConfig();
            case 'fall': return this._fallConfig();
            case 'halloween': return this._halloweenConfig();
            default: return null;
        }
    },

    _winterConfig() {
        const COUNT = 30;
        const SNOWMAN_COUNT = 4;
        return {
            specialInterval: 500,
            init(state, W, H, makeEl) {
                for (let i = 0; i < COUNT; i++) {
                    const size = 12 + Math.random() * 10;
                    state.particles.push({
                        el: makeEl('❄️', size),
                        x: Math.random() * W,
                        y: Math.random() * H,
                        size,
                        speed: 0.15 + Math.random() * 0.35,
                        driftAmp: 8 + Math.random() * 12,
                        driftIdx: Math.floor(Math.random() * 360),
                        driftSpd: 1 + Math.floor(Math.random() * 3),
                        rot: Math.random() * 360,
                        rotSpd: Math.random() * 0.8 - 0.4,
                        alpha: 0.7 + Math.random() * 0.3
                    });
                }
            },
            update(state, W, H, frame, makeEl, removeEl, posEl, sin) {
                state.particles.forEach((p, i) => {
                    p.y += p.speed;
                    p.driftIdx = (p.driftIdx + p.driftSpd) % 360;
                    p.x += sin[p.driftIdx] * p.driftAmp * 0.015;
                    p.rot += p.rotSpd;
                    if (p.y > H + p.size) { p.y = -p.size * 2; p.x = Math.random() * W; }
                    if (p.x < -p.size) p.x = W + p.size;
                    else if (p.x > W + p.size) p.x = -p.size;
                    posEl(p.el, p.x, p.y, p.alpha, `rotate(${p.rot}deg)`);
                });

                state.specialTimer++;
                const active = state.specials.filter(s => s.state !== 'done');
                if (state.specialTimer >= state.specialInterval && active.length === 0) {
                    state.specialTimer = 0;
                    const groundY = H - 40;
                    const spacing = W / (SNOWMAN_COUNT + 1);
                    for (let i = 0; i < SNOWMAN_COUNT; i++) {
                        state.specials.push({
                            el: makeEl('⛄', 35),
                            x: spacing * (i + 1) + Math.random() * 40 - 20,
                            y: H + 40,
                            vy: 0,
                            groundY,
                            state: 'wait',
                            alpha: 1,
                            bounces: 0,
                            wait: i * 30 + Math.floor(Math.random() * 40)
                        });
                    }
                }

                for (let i = state.specials.length - 1; i >= 0; i--) {
                    const s = state.specials[i];
                    switch (s.state) {
                        case 'wait':
                            s.wait--;
                            if (s.wait <= 0) { s.state = 'rise'; s.vy = -3; }
                            posEl(s.el, s.x, s.y, 0);
                            break;
                        case 'rise':
                            s.vy += 0.15;
                            s.y += s.vy;
                            if (s.y >= s.groundY) {
                                s.y = s.groundY;
                                s.bounces++;
                                s.state = s.bounces >= 1 ? 'fade' : 'rise';
                                s.vy = s.bounces >= 1 ? 0 : -3 * 0.3;
                            }
                            posEl(s.el, s.x, s.y, 1);
                            break;
                        case 'fade':
                            s.alpha -= 0.005;
                            if (s.alpha <= 0) { s.state = 'done'; removeEl(s.el); }
                            else posEl(s.el, s.x, s.y, s.alpha);
                            break;
                        case 'done':
                            break;
                    }
                }
                state.specials = state.specials.filter(s => s.state !== 'done');
            }
        };
    },

    _springConfig() {
        const PETAL_COUNT = 20;
        const BEE_COUNT = 3;
        return {
            specialInterval: 500,
            init(state, W, H, makeEl) {
                for (let i = 0; i < PETAL_COUNT; i++) {
                    const size = 14 + Math.random() * 10;
                    state.particles.push({
                        el: makeEl(Math.random() > 0.2 ? '🌸' : '🌼', size),
                        x: Math.random() * W,
                        y: Math.random() * H,
                        size,
                        speed: 0.1 + Math.random() * 0.2,
                        driftAmp: 15 + Math.random() * 20,
                        driftIdx: Math.floor(Math.random() * 360),
                        driftSpd: 1 + Math.floor(Math.random() * 2),
                        rot: Math.random() * 360,
                        rotSpd: 0.1 + Math.random() * 0.4,
                        alpha: 0.7 + Math.random() * 0.3
                    });
                }
            },
            update(state, W, H, frame, makeEl, removeEl, posEl, sin) {
                state.particles.forEach(p => {
                    p.y += p.speed;
                    p.driftIdx = (p.driftIdx + p.driftSpd) % 360;
                    p.x += sin[p.driftIdx] * p.driftAmp * 0.012;
                    p.rot += p.rotSpd;
                    if (p.y > H + p.size) { p.y = -p.size * 2; p.x = Math.random() * W; }
                    if (p.x < -p.size) p.x = W + p.size;
                    else if (p.x > W + p.size) p.x = -p.size;
                    posEl(p.el, p.x, p.y, p.alpha, `rotate(${p.rot}deg)`);
                });

                // Bees fly side to side with vertical buzz
                state.specialTimer++;
                const activeBees = state.specials.filter(s => s.state !== 'done');
                if (state.specialTimer >= state.specialInterval && activeBees.length === 0) {
                    state.specialTimer = 0;
                    const usableH = H * 0.6;
                    const topMargin = H * 0.2;
                    const zoneH = usableH / BEE_COUNT;
                    for (let i = 0; i < BEE_COUNT; i++) {
                        const fromLeft = Math.random() > 0.5;
                        const startX = fromLeft ? -40 : W + 40;
                        const baseY = topMargin + zoneH * i + zoneH * 0.2 + Math.random() * (zoneH * 0.6);
                        state.specials.push({
                            el: makeEl('🐝', 24),
                            x: startX,
                            y: baseY,
                            targetX: fromLeft ? W + 40 : -40,
                            speed: 0.6 + Math.random() * 0.4,
                            state: 'wait',
                            alpha: 1,
                            wait: i * 30 + 15 + Math.floor(Math.random() * 30),
                            buzzIdx: Math.floor(Math.random() * 360),
                            buzzSpd: 4 + Math.floor(Math.random() * 4),
                            buzzAmp: 1.2 + Math.random() * 0.8,
                            fromLeft
                        });
                    }
                }

                for (let i = state.specials.length - 1; i >= 0; i--) {
                    const s = state.specials[i];
                    switch (s.state) {
                        case 'wait':
                            s.wait--;
                            if (s.wait <= 0) s.state = 'fly';
                            posEl(s.el, s.x, s.y, 0);
                            break;
                        case 'fly':
                            s.x += s.fromLeft ? s.speed : -s.speed;
                            s.buzzIdx = (s.buzzIdx + s.buzzSpd) % 360;
                            s.y += sin[s.buzzIdx] * s.buzzAmp * 0.3;
                            const reached = s.fromLeft ? s.x > s.targetX : s.x < s.targetX;
                            if (reached) s.state = 'fade';
                            posEl(s.el, s.x, s.y, 1, s.fromLeft ? 'scaleX(-1)' : '');
                            break;
                        case 'fade':
                            s.alpha -= 0.01;
                            if (s.alpha <= 0) { s.state = 'done'; removeEl(s.el); }
                            else posEl(s.el, s.x, s.y, s.alpha);
                            break;
                        case 'done': break;
                    }
                }
                state.specials = state.specials.filter(s => s.state !== 'done');
            }
        };
    },

    _summerConfig() {
        const BALL_COUNT = 2;
        const SUN_MAX = 2;
        const UMBRELLA_COUNT = 3;
        return {
            specialInterval: 400,
            init(state, W, H, makeEl) {
                state._sunTimer = 0;
                state._sunInterval = 250;
                state._umbrellaTimer = 0;
                state._umbrellaInterval = 600;
                state._suns = [];
                state._umbrellas = [];
            },
            update(state, W, H, frame, makeEl, removeEl, posEl, sin) {
                // Beach balls bounce side to side
                state.specialTimer++;
                const activeBalls = state.specials.filter(s => s.state !== 'done');
                if (state.specialTimer >= state.specialInterval && activeBalls.length === 0) {
                    state.specialTimer = 0;
                    const usableH = H * 0.5;
                    const topMargin = H * 0.3;
                    const zoneH = usableH / BALL_COUNT;
                    for (let i = 0; i < BALL_COUNT; i++) {
                        const fromLeft = Math.random() > 0.5;
                        const startX = fromLeft ? -50 : W + 50;
                        const baseY = topMargin + zoneH * i + zoneH * 0.3 + Math.random() * (zoneH * 0.4);
                        state.specials.push({
                            el: makeEl('🏐', 28),
                            x: startX,
                            y: baseY,
                            baseY,
                            targetX: fromLeft ? W + 50 : -50,
                            speed: 0.5 + Math.random() * 0.3,
                            state: 'wait',
                            alpha: 1,
                            wait: i * 40 + 15 + Math.floor(Math.random() * 35),
                            bounceIdx: Math.floor(Math.random() * 360),
                            bounceSpd: 1 + Math.floor(Math.random() * 2),
                            bounceAmp: 35 + Math.random() * 15,
                            fromLeft
                        });
                    }
                }

                for (let i = state.specials.length - 1; i >= 0; i--) {
                    const s = state.specials[i];
                    switch (s.state) {
                        case 'wait':
                            s.wait--;
                            if (s.wait <= 0) s.state = 'bounce';
                            posEl(s.el, s.x, s.y, 0);
                            break;
                        case 'bounce':
                            s.x += s.fromLeft ? s.speed : -s.speed;
                            s.bounceIdx = (s.bounceIdx + s.bounceSpd) % 360;
                            s.y = s.baseY + sin[s.bounceIdx] * s.bounceAmp * 0.3;
                            const reached = s.fromLeft ? s.x > W + 50 : s.x < -50;
                            if (reached) s.state = 'fade';
                            posEl(s.el, s.x, s.y, 1, `rotate(${s.bounceIdx * 2}deg)`);
                            break;
                        case 'fade':
                            s.alpha -= 0.01;
                            if (s.alpha <= 0) { s.state = 'done'; removeEl(s.el); }
                            else posEl(s.el, s.x, s.y, s.alpha);
                            break;
                        case 'done': break;
                    }
                }
                state.specials = state.specials.filter(s => s.state !== 'done');

                // Suns: appear, pulse, fade
                state._sunTimer++;
                const activeSuns = state._suns.filter(s => s.state !== 'done');
                if (state._sunTimer >= state._sunInterval && activeSuns.length < SUN_MAX) {
                    state._sunTimer = 0;
                    const x = 50 + Math.random() * (W - 100);
                    const y = 50 + Math.random() * (H * 0.4);
                    state._suns.push({
                        el: makeEl('☀️', 32),
                        x, y,
                        state: 'wait',
                        alpha: 0,
                        wait: Math.floor(Math.random() * 60),
                        scale: 0.5,
                        pulses: 0
                    });
                }

                for (let i = state._suns.length - 1; i >= 0; i--) {
                    const s = state._suns[i];
                    switch (s.state) {
                        case 'wait':
                            s.wait--;
                            if (s.wait <= 0) s.state = 'pulseIn';
                            posEl(s.el, s.x, s.y, 0);
                            break;
                        case 'pulseIn':
                            s.alpha = Math.min(1, s.alpha + 0.012);
                            s.scale = Math.min(1.2, s.scale + 0.006);
                            if (s.scale >= 1.2) s.state = 'pulseOut';
                            posEl(s.el, s.x, s.y, s.alpha, `scale(${s.scale})`);
                            break;
                        case 'pulseOut':
                            s.scale = Math.max(0.8, s.scale - 0.006);
                            if (s.scale <= 0.8) {
                                s.pulses++;
                                s.state = s.pulses >= 2 ? 'fade' : 'pulseIn';
                            }
                            posEl(s.el, s.x, s.y, s.alpha, `scale(${s.scale})`);
                            break;
                        case 'fade':
                            s.alpha -= 0.005;
                            s.scale -= 0.004;
                            if (s.alpha <= 0) { s.state = 'done'; removeEl(s.el); }
                            else posEl(s.el, s.x, s.y, s.alpha, `scale(${s.scale})`);
                            break;
                        case 'done': break;
                    }
                }
                state._suns = state._suns.filter(s => s.state !== 'done');

                // Umbrellas: pop up from bottom, settle, fade
                state._umbrellaTimer++;
                const activeUmbrellas = state._umbrellas.filter(s => s.state !== 'done');
                if (state._umbrellaTimer >= state._umbrellaInterval && activeUmbrellas.length === 0) {
                    state._umbrellaTimer = 0;
                    const groundY = H - 40;
                    const spacing = W / (UMBRELLA_COUNT + 1);
                    for (let i = 0; i < UMBRELLA_COUNT; i++) {
                        state._umbrellas.push({
                            el: makeEl('⛱️', 32),
                            x: spacing * (i + 1) + Math.random() * 40 - 20,
                            y: H + 40,
                            vy: -1.5,
                            groundY,
                            state: 'wait',
                            alpha: 1,
                            wait: i * 25 + Math.floor(Math.random() * 30)
                        });
                    }
                }

                for (let i = state._umbrellas.length - 1; i >= 0; i--) {
                    const s = state._umbrellas[i];
                    switch (s.state) {
                        case 'wait':
                            s.wait--;
                            if (s.wait <= 0) s.state = 'rise';
                            posEl(s.el, s.x, s.y, 0);
                            break;
                        case 'rise':
                            s.y += s.vy;
                            if (s.y <= s.groundY) { s.y = s.groundY; s.state = 'settle'; }
                            posEl(s.el, s.x, s.y, 1);
                            break;
                        case 'settle':
                            s.state = 'fade';
                            posEl(s.el, s.x, s.y, 1);
                            break;
                        case 'fade':
                            s.alpha -= 0.005;
                            if (s.alpha <= 0) { s.state = 'done'; removeEl(s.el); }
                            else posEl(s.el, s.x, s.y, s.alpha);
                            break;
                        case 'done': break;
                    }
                }
                state._umbrellas = state._umbrellas.filter(s => s.state !== 'done');
            }
        };
    },

    _fallConfig() {
        const LEAF_COUNT = 18;
        const PUMPKIN_COUNT = 4;
        const leafHues = [0, -20, 30]; // orange, red-ish, yellow-brown via hue-rotate
        return {
            specialInterval: 500,
            init(state, W, H, makeEl) {
                for (let i = 0; i < LEAF_COUNT; i++) {
                    const size = 16 + Math.random() * 10;
                    const colorIdx = Math.floor(Math.random() * 3);
                    const el = makeEl('🍁', size);
                    if (leafHues[colorIdx] !== 0) {
                        el.style.filter = `hue-rotate(${leafHues[colorIdx]}deg)`;
                    }
                    state.particles.push({
                        el,
                        x: Math.random() * W,
                        y: Math.random() * H,
                        size,
                        speed: 0.1 + Math.random() * 0.2,
                        driftAmp: 20 + Math.random() * 25,
                        driftIdx: Math.floor(Math.random() * 360),
                        driftSpd: 1 + Math.floor(Math.random() * 2),
                        rot: Math.random() * 360,
                        rotSpd: 0.1 + Math.random() * 0.4,
                        alpha: 0.8 + Math.random() * 0.2
                    });
                }
            },
            update(state, W, H, frame, makeEl, removeEl, posEl, sin) {
                state.particles.forEach(p => {
                    p.y += p.speed;
                    p.driftIdx = (p.driftIdx + p.driftSpd) % 360;
                    p.x += sin[p.driftIdx] * p.driftAmp * 0.01;
                    p.rot += p.rotSpd;
                    if (p.y > H + p.size) { p.y = -p.size * 2; p.x = Math.random() * W; }
                    if (p.x < -p.size) p.x = W + p.size;
                    else if (p.x > W + p.size) p.x = -p.size;
                    posEl(p.el, p.x, p.y, p.alpha, `rotate(${p.rot}deg)`);
                });

                // Pumpkins pop up from bottom, bounce, fade
                state.specialTimer++;
                const active = state.specials.filter(s => s.state !== 'done');
                if (state.specialTimer >= state.specialInterval && active.length === 0) {
                    state.specialTimer = 0;
                    const groundY = H - 40;
                    const spacing = W / (PUMPKIN_COUNT + 1);
                    for (let i = 0; i < PUMPKIN_COUNT; i++) {
                        state.specials.push({
                            el: makeEl('🎃', 32),
                            x: spacing * (i + 1) + Math.random() * 40 - 20,
                            y: H + 40,
                            vy: 0,
                            groundY,
                            state: 'wait',
                            alpha: 1,
                            bounces: 0,
                            wait: i * 30 + Math.floor(Math.random() * 40)
                        });
                    }
                }

                for (let i = state.specials.length - 1; i >= 0; i--) {
                    const s = state.specials[i];
                    switch (s.state) {
                        case 'wait':
                            s.wait--;
                            if (s.wait <= 0) { s.state = 'rise'; s.vy = -3; }
                            posEl(s.el, s.x, s.y, 0);
                            break;
                        case 'rise':
                            s.vy += 0.15;
                            s.y += s.vy;
                            if (s.y >= s.groundY) {
                                s.y = s.groundY;
                                s.bounces++;
                                s.state = s.bounces >= 1 ? 'fade' : 'rise';
                                s.vy = s.bounces >= 1 ? 0 : -3 * 0.3;
                            }
                            posEl(s.el, s.x, s.y, 1);
                            break;
                        case 'fade':
                            s.alpha -= 0.005;
                            if (s.alpha <= 0) { s.state = 'done'; removeEl(s.el); }
                            else posEl(s.el, s.x, s.y, s.alpha);
                            break;
                        case 'done': break;
                    }
                }
                state.specials = state.specials.filter(s => s.state !== 'done');
            }
        };
    },

    _halloweenConfig() {
        const CANDY_COUNT = 12;
        const GHOST_COUNT = 3;
        const PUMPKIN_COUNT = 3;
        const MAX_SPIDERS = 2;
        const candyHues = [0, 60, 180, 270]; // red, yellow, teal, purple via hue-rotate
        return {
            specialInterval: 500,
            init(state, W, H, makeEl) {
                state._ghostTimer = 0;
                state._ghostInterval = 500;
                state._ghosts = [];
                state._pumpkinTimer = 0;
                state._pumpkinInterval = 650;
                state._pumpkins = [];
                state._spiderTimer = 0;
                state._spiderInterval = 250;
                state._spiders = [];

                for (let i = 0; i < CANDY_COUNT; i++) {
                    const size = 12 + Math.random() * 6;
                    const el = makeEl('🍬', size);
                    const hue = candyHues[Math.floor(Math.random() * candyHues.length)];
                    if (hue !== 0) el.style.filter = `hue-rotate(${hue}deg)`;
                    state.particles.push({
                        el,
                        x: Math.random() * W,
                        y: Math.random() * H,
                        size,
                        speed: 0.12 + Math.random() * 0.2,
                        driftAmp: 8 + Math.random() * 10,
                        driftIdx: Math.floor(Math.random() * 360),
                        driftSpd: 1 + Math.floor(Math.random() * 2),
                        alpha: 0.7 + Math.random() * 0.3
                    });
                }
            },
            update(state, W, H, frame, makeEl, removeEl, posEl, sin) {
                // Candy falls
                state.particles.forEach(p => {
                    p.y += p.speed;
                    p.driftIdx = (p.driftIdx + p.driftSpd) % 360;
                    p.x += sin[p.driftIdx] * p.driftAmp * 0.012;
                    if (p.y > H + p.size) { p.y = -p.size * 2; p.x = Math.random() * W; }
                    posEl(p.el, p.x, p.y, p.alpha);
                });

                // Ghosts float side to side
                state._ghostTimer++;
                const activeGhosts = state._ghosts.filter(s => s.state !== 'done');
                if (state._ghostTimer >= state._ghostInterval && activeGhosts.length === 0) {
                    state._ghostTimer = 0;
                    const usableH = H * 0.5;
                    const topMargin = H * 0.15;
                    const zoneH = usableH / GHOST_COUNT;
                    for (let i = 0; i < GHOST_COUNT; i++) {
                        const fromLeft = Math.random() > 0.5;
                        const startX = fromLeft ? -55 : W + 55;
                        const baseY = topMargin + zoneH * i + Math.random() * (zoneH * 0.6);
                        state._ghosts.push({
                            el: makeEl('👻', 30),
                            x: startX,
                            y: baseY,
                            baseY,
                            speed: 0.5 + Math.random() * 0.3,
                            state: 'wait',
                            alpha: 0.8,
                            wait: i * 45 + 15 + Math.floor(Math.random() * 45),
                            floatIdx: Math.floor(Math.random() * 360),
                            floatSpd: 1 + Math.floor(Math.random() * 2),
                            floatAmp: 12 + Math.random() * 8,
                            fromLeft
                        });
                    }
                }

                for (let i = state._ghosts.length - 1; i >= 0; i--) {
                    const s = state._ghosts[i];
                    switch (s.state) {
                        case 'wait':
                            s.wait--;
                            if (s.wait <= 0) s.state = 'float';
                            posEl(s.el, s.x, s.y, 0);
                            break;
                        case 'float':
                            s.x += s.fromLeft ? s.speed : -s.speed;
                            s.floatIdx = (s.floatIdx + s.floatSpd) % 360;
                            s.y = s.baseY + sin[s.floatIdx] * s.floatAmp;
                            const reached = s.fromLeft ? s.x > W + 55 : s.x < -55;
                            if (reached) s.state = 'fade';
                            posEl(s.el, s.x, s.y, s.alpha);
                            break;
                        case 'fade':
                            s.alpha -= 0.01;
                            if (s.alpha <= 0) { s.state = 'done'; removeEl(s.el); }
                            else posEl(s.el, s.x, s.y, s.alpha);
                            break;
                        case 'done': break;
                    }
                }
                state._ghosts = state._ghosts.filter(s => s.state !== 'done');

                // Pumpkins pop up from bottom
                if (frame % 2 === 0) {
                    state._pumpkinTimer++;
                    const activePumpkins = state._pumpkins.filter(s => s.state !== 'done');
                    if (state._pumpkinTimer >= state._pumpkinInterval && activePumpkins.length === 0) {
                        state._pumpkinTimer = 0;
                        const groundY = H - 40;
                        const spacing = W / (PUMPKIN_COUNT + 1);
                        for (let i = 0; i < PUMPKIN_COUNT; i++) {
                            state._pumpkins.push({
                                el: makeEl('🎃', 32),
                                x: spacing * (i + 1) + Math.random() * 40 - 20,
                                y: H + 40,
                                vy: 0,
                                groundY,
                                state: 'wait',
                                alpha: 1,
                                bounces: 0,
                                wait: i * 30 + Math.floor(Math.random() * 40)
                            });
                        }
                    }

                    for (let i = state._pumpkins.length - 1; i >= 0; i--) {
                        const s = state._pumpkins[i];
                        switch (s.state) {
                            case 'wait':
                                s.wait--;
                                if (s.wait <= 0) { s.state = 'rise'; s.vy = -3; }
                                posEl(s.el, s.x, s.y, 0);
                                break;
                            case 'rise':
                                s.vy += 0.15;
                                s.y += s.vy;
                                if (s.y >= s.groundY) {
                                    s.y = s.groundY;
                                    s.bounces++;
                                    s.state = s.bounces >= 1 ? 'fade' : 'rise';
                                    s.vy = s.bounces >= 1 ? 0 : -3 * 0.3;
                                }
                                posEl(s.el, s.x, s.y, 1);
                                break;
                            case 'fade':
                                s.alpha -= 0.005;
                                if (s.alpha <= 0) { s.state = 'done'; removeEl(s.el); }
                                else posEl(s.el, s.x, s.y, s.alpha);
                                break;
                            case 'done': break;
                        }
                    }
                    state._pumpkins = state._pumpkins.filter(s => s.state !== 'done');
                }

                // Spiders appear/disappear at random spots
                if (frame % 3 === 0) {
                    state._spiderTimer++;
                    const activeSpiders = state._spiders.filter(s => s.state !== 'done');
                    if (state._spiderTimer >= state._spiderInterval && activeSpiders.length < MAX_SPIDERS) {
                        state._spiderTimer = 0;
                        state._spiders.push({
                            el: makeEl('🕷️', 26),
                            x: 50 + Math.random() * (W - 100),
                            y: 50 + Math.random() * (H - 100),
                            state: 'wait',
                            alpha: 0,
                            wait: Math.floor(Math.random() * 30),
                            visibleTimer: 90 + Math.floor(Math.random() * 60)
                        });
                    }

                    for (let i = state._spiders.length - 1; i >= 0; i--) {
                        const s = state._spiders[i];
                        switch (s.state) {
                            case 'wait':
                                s.wait--;
                                if (s.wait <= 0) s.state = 'appear';
                                posEl(s.el, s.x, s.y, 0);
                                break;
                            case 'appear':
                                s.alpha = Math.min(1, s.alpha + 0.012);
                                if (s.alpha >= 1) s.state = 'visible';
                                posEl(s.el, s.x, s.y, s.alpha);
                                break;
                            case 'visible':
                                s.visibleTimer--;
                                if (s.visibleTimer <= 0) s.state = 'disappear';
                                posEl(s.el, s.x, s.y, 1);
                                break;
                            case 'disappear':
                                s.alpha -= 0.008;
                                if (s.alpha <= 0) { s.state = 'done'; removeEl(s.el); }
                                else posEl(s.el, s.x, s.y, s.alpha);
                                break;
                            case 'done': break;
                        }
                    }
                    state._spiders = state._spiders.filter(s => s.state !== 'done');
                }
            }
        };
    },

    setupGlobalListeners() {
        // Centralized back button handler — checks overlays top-down
        // so only the topmost one closes per press
        window.addEventListener('popstate', function(e) {
            // If state still has moonfinDetails, a Jellyfin dialog just closed
            // (dialogHelper pushes/pops its own history entry) — don't close our overlay
            var state = e.state || history.state || {};
            if (Details.isVisible) {
                if (state.moonfinDetails) return;
                Details.hide(true);
            } else if (Settings.isOpen) {
                Settings.hide(true);
            } else if (Jellyseerr.isOpen) {
                Jellyseerr.close(true);
                Navbar.updateJellyseerrButtonState();
            } else if (Library.isVisible) {
                Library.close();
            } else if (Genres.isVisible) {
                if (Genres.currentView === 'browse') {
                    Genres.showGrid();
                    history.pushState({ moonfinGenres: true }, '');
                } else {
                    Genres.close();
                }
            }
        });

        window.addEventListener('viewshow', () => {
            this.onPageChange();
        });
        
        window.addEventListener('hashchange', () => {
            this.onPageChange();
        });

        this.setupDOMObserver();

        window.addEventListener('moonfin-settings-preview', (e) => {
            if (Navbar.initialized) Navbar.applySettings(e.detail);
            if (Sidebar.initialized) Sidebar.applySettings(e.detail);
            MediaBar.applySettings(e.detail);
        });

        window.addEventListener('moonfin-settings-changed', (e) => {
            console.log('[Moonfin] Settings changed:', e.detail);

            var navEnabled = e.detail.navbarEnabled;
            var navPosition = e.detail.navbarPosition || 'top';

            if (navEnabled) {
                if (navPosition === 'left') {
                    if (Navbar.initialized) Navbar.destroy();
                    if (!Sidebar.initialized) Sidebar.init();
                } else {
                    if (Sidebar.initialized) Sidebar.destroy();
                    if (!Navbar.initialized) Navbar.init();
                }
            } else {
                if (Navbar.initialized) Navbar.destroy();
                if (Sidebar.initialized) Sidebar.destroy();
            }

            if (e.detail.mediaBarEnabled && !MediaBar.initialized) {
                MediaBar.init();
            } else if (!e.detail.mediaBarEnabled && MediaBar.initialized) {
                MediaBar.destroy();
            }
        });
    },

    onPageChange() {
        if (Details.isVisible) {
            Details.hide(true);
        }

        if (Jellyseerr.isOpen) {
            Jellyseerr.close(true);
            if (Navbar.initialized) Navbar.updateJellyseerrButtonState();
            if (Sidebar.initialized) Sidebar.updateJellyseerrButtonState();
        }

        if (Genres.isVisible) {
            Genres.close();
        }

        if (Library.isVisible) {
            Library.close();
        }

        if (Settings.isOpen) {
            Settings.hide(true);
        }

        if (this.isAdminPage()) {
            if (Navbar.container) Navbar.container.classList.add('hidden');
            if (Sidebar.container) Sidebar.container.classList.add('hidden');
            if (Sidebar.mobileTrigger) Sidebar.mobileTrigger.classList.add('hidden');
            if (MediaBar.container) MediaBar.container.classList.add('hidden');
            document.querySelectorAll('.moonfin-seasonal-effect').forEach(el => el.style.display = 'none');
            document.body.classList.remove('moonfin-navbar-active');
            document.body.classList.remove('moonfin-sidebar-active');
            return;
        }

        var hash = window.location.hash || '';
        if (hash.includes('#/video')) {
            if (Navbar.container) Navbar.container.classList.add('hidden');
            if (Sidebar.container) Sidebar.container.classList.add('hidden');
            if (Sidebar.mobileTrigger) Sidebar.mobileTrigger.classList.add('hidden');
            if (MediaBar.container) MediaBar.container.classList.add('hidden');
            document.body.classList.remove('moonfin-navbar-active');
            document.body.classList.remove('moonfin-sidebar-active');
            document.body.classList.remove('moonfin-mediabar-active');
            return;
        }

        if (!this.initialized) {
            this.init();
            return;
        }

        if (Navbar.container) {
            var navbarEnabled = Storage.get('navbarEnabled') && Storage.get('navbarPosition') !== 'left';
            Navbar.container.classList.toggle('hidden', !navbarEnabled);
            document.body.classList.toggle('moonfin-navbar-active', !!navbarEnabled);
        }

        if (Sidebar.container) {
            var sidebarEnabled = Storage.get('navbarEnabled') && Storage.get('navbarPosition') === 'left';
            Sidebar.container.classList.toggle('hidden', !sidebarEnabled);
            if (Sidebar.mobileTrigger) Sidebar.mobileTrigger.classList.toggle('hidden', !sidebarEnabled);
            document.body.classList.toggle('moonfin-sidebar-active', !!sidebarEnabled);
        }

        document.querySelectorAll('.moonfin-seasonal-effect').forEach(el => el.style.display = '');

        if (MediaBar.initialized && MediaBar.container) {
            MediaBar.ensureInDOM();

            var showMediaBar = this.isHomePage();
            MediaBar.container.classList.toggle('hidden', !showMediaBar);
            if (MediaBar.items && MediaBar.items.length > 0) {
                document.body.classList.toggle('moonfin-mediabar-active', showMediaBar);
            } else {
                document.body.classList.remove('moonfin-mediabar-active');
            }
        } else {
            document.body.classList.remove('moonfin-mediabar-active');
        }

        Navbar.updateActiveState();
        if (Sidebar.initialized) Sidebar.updateActiveState();

        if ((window.location.hash || '').toLowerCase().includes('mypreferencesmenu')) {
            var self = this;
            var attempts = 0;
            var tryInject = function() {
                self.addUserPreferencesLink();
                attempts++;
                if (attempts < 5 && !document.querySelector('.moonfin-prefs-link')) {
                    setTimeout(tryInject, 300);
                }
            };
            tryInject();
        }
    },

    addUserPreferencesLink() {
        var prefsPage = document.querySelector('#myPreferencesMenuPage:not(.hide)') ||
                        document.querySelector('.myPreferencesMenuPage:not(.hide)') ||
                        document.querySelector('[data-page="mypreferencesmenu"]:not(.hide)');

        if (!prefsPage) {
            var pages = document.querySelectorAll('.page:not(.hide)');
            for (var p = 0; p < pages.length; p++) {
                if (pages[p].querySelector('.listItem-border, .listItem')) {
                    var hash = (window.location.hash || '').toLowerCase();
                    if (hash.includes('mypreferencesmenu')) {
                        prefsPage = pages[p];
                        break;
                    }
                }
            }
        }

        if (!prefsPage) return;
        if (prefsPage.querySelector('.moonfin-prefs-link')) return;

        var menuItems = prefsPage.querySelectorAll('.listItem-border');
        if (menuItems.length === 0) {
            menuItems = prefsPage.querySelectorAll('.listItem');
        }
        if (menuItems.length === 0) return;

        var insertAfter = null;
        for (var i = 0; i < menuItems.length; i++) {
            var text = (menuItems[i].textContent || '').trim().toLowerCase();
            if (text.includes('control')) {
                insertAfter = menuItems[i];
                break;
            }
        }
        if (!insertAfter && menuItems.length >= 2) {
            insertAfter = menuItems[menuItems.length - 2];
        }
        if (!insertAfter) {
            insertAfter = menuItems[menuItems.length - 1];
        }
        while (insertAfter.parentNode && insertAfter.parentNode !== prefsPage && !insertAfter.parentNode.querySelector('.listItem-border, .listItem')) {
            insertAfter = insertAfter.parentNode;
        }
        if (!insertAfter || !insertAfter.parentNode) return;

        var link = document.createElement('a');
        link.className = 'listItem-border moonfin-prefs-link';
        link.href = '#';
        link.style.cssText = 'display:block;margin:0;padding:0;text-decoration:none;color:inherit;';
        link.innerHTML =
            '<div class="listItem" style="cursor:pointer">' +
                '<span class="material-icons listItemIcon listItemIcon-transparent settings" aria-hidden="true"></span>' +
                '<div class="listItemBody">' +
                    '<div class="listItemBodyText">Moonfin</div>' +
                '</div>' +
            '</div>';

        link.addEventListener('click', function(e) {
            e.preventDefault();
            e.stopPropagation();
            Settings.show();
        });

        insertAfter.parentNode.insertBefore(link, insertAfter.nextSibling);
    },

    setupDOMObserver() {
        if (this._domObserver) return;

        var self = this;
        var throttleTimer = null;

        this._domObserver = new MutationObserver(function() {
            if (throttleTimer) return;
            throttleTimer = setTimeout(function() {
                throttleTimer = null;
                var hash = window.location.hash.toLowerCase();
                if (hash.includes('mypreferencesmenu')) {
                    self.addUserPreferencesLink();
                }
            }, 200);
        });

        this._domObserver.observe(document.body, {
            childList: true,
            subtree: true
        });
    },

    destroy() {
        Navbar.destroy();
        MediaBar.destroy();
        Jellyseerr.destroy();
        document.querySelectorAll('.moonfin-seasonal-effect').forEach(el => el.remove());
        this.initialized = false;
        console.log('[Moonfin] Plugin destroyed');
    }
};

(function() {
    if (typeof window !== 'undefined') {
        const isUserLoggedIn = () => {
            try {
                const api = window.ApiClient || (window.connectionManager && window.connectionManager.currentApiClient());
                return api && 
                       api._currentUser && 
                       api._currentUser.Id &&
                       api._serverInfo && 
                       api._serverInfo.AccessToken;
            } catch (e) {
                return false;
            }
        };
        
        const initWhenReady = () => {
            const hash = window.location.hash.toLowerCase();
            if (hash.includes('login') || hash.includes('selectserver') || hash.includes('startup')) {
                setTimeout(initWhenReady, 1000);
                return;
            }
            
            if (isUserLoggedIn()) {
                console.log('[Moonfin] User authenticated, initializing...');
                Plugin.init();
            } else {
                console.log('[Moonfin] Waiting for user authentication...');
                setTimeout(initWhenReady, 500);
            }
        };

        if (document.readyState === 'complete') {
            setTimeout(initWhenReady, 100);
        } else {
            window.addEventListener('load', () => setTimeout(initWhenReady, 100));
        }
        
        window.addEventListener('hashchange', () => {
            if (isUserLoggedIn() && !Plugin.initialized) {
                Plugin.init();
            }
        });
    }

    window.Moonfin = {
        Plugin,
        TVNavigation,
        Device,
        Storage,
        Navbar,
        MediaBar,
        Jellyseerr,
        Details,
        API
    };
})();
