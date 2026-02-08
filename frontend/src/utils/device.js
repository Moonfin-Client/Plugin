const Device = {
    _cache: null,

    detect() {
        if (this._cache) return this._cache;

        const ua = navigator.userAgent.toLowerCase();
        const width = window.innerWidth;
        const hasTouch = 'ontouchstart' in window || navigator.maxTouchPoints > 0;

        const isMobile = /android|webos|iphone|ipad|ipod|blackberry|iemobile|opera mini|mobile/i.test(ua);
        const isTablet = /ipad|android(?!.*mobile)|tablet/i.test(ua) || (hasTouch && width >= 768 && width <= 1024);
        const isTV = /tv|tizen|webos|smart-tv|netcast|hbbtv|vidaa|viera/i.test(ua);
        const isDesktop = !isMobile && !isTablet && !isTV;

        this._cache = {
            type: isTV ? 'tv' : isTablet ? 'tablet' : isMobile ? 'mobile' : 'desktop',
            isMobile: isMobile || isTablet,
            isDesktop,
            isTV,
            isTablet,
            hasTouch,
            screenWidth: width,
            screenHeight: window.innerHeight,
            userAgent: navigator.userAgent
        };

        console.log('[Moonfin] Device detected:', this._cache.type);
        return this._cache;
    },

    isMobile() {
        return this.detect().isMobile;
    },

    isDesktop() {
        return this.detect().isDesktop;
    },

    isTV() {
        return this.detect().isTV;
    },

    hasTouch() {
        return this.detect().hasTouch;
    },

    getInfo() {
        return this.detect();
    }
};
