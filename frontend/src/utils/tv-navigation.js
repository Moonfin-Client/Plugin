const TVNavigation = {
    enabled: false,
    focusableSelector: '.moonfin-focusable, .moonfin-nav-btn, .moonfin-user-btn, .moonfin-library-btn, .moonfin-mediabar-nav-btn, .moonfin-mediabar-dot, .moonfin-jellyseerr-fab',
    focusableElements: [],
    
    // Key codes for different TV platforms
    KEYS: {
        LEFT: [37, 'ArrowLeft'],
        RIGHT: [39, 'ArrowRight'],
        UP: [38, 'ArrowUp'],
        DOWN: [40, 'ArrowDown'],
        ENTER: [13, 'Enter'],
        BACK: [461, 10009, 8, 27, 'Escape', 'GoBack'],
    },

    init() {
        if (!this.isTV()) {
            console.log('[Moonfin TV] Not a TV device, skipping TV navigation');
            return;
        }

        console.log('[Moonfin TV] Initializing TV navigation...');
        this.enabled = true;
        
        document.body.classList.add('moonfin-tv-mode');
        
        this.setupKeyboardListeners();
        
        this.setupMutationObserver();
        
        this.updateFocusableElements();
        
        console.log('[Moonfin TV] TV navigation initialized');
    },

    isTV() {
        // Check NativeShell (jellyfin-webos/tizen provides this)
        if (window.NativeShell?.AppHost?.getDefaultLayout?.() === 'tv') {
            return true;
        }
        
        const ua = navigator.userAgent.toLowerCase();
        if (/tv|tizen|webos|smart-tv|netcast|hbbtv|vidaa|viera/i.test(ua)) {
            return true;
        }
        
        // Check Device utility
        if (typeof Device !== 'undefined' && Device.isTV?.()) {
            return true;
        }
        
        return false;
    },

    setupKeyboardListeners() {
        // Use capture phase to intercept events before jellyfin-web handlers
        document.addEventListener('keydown', (e) => {
            if (!this.enabled) return;
            
            const key = e.key || e.keyCode;
            
            const activeEl = document.activeElement;
            const isMoonfinElement = activeEl && (
                activeEl.classList.contains('moonfin-nav-btn') ||
                activeEl.classList.contains('moonfin-user-btn') ||
                activeEl.classList.contains('moonfin-mediabar-nav-btn') ||
                activeEl.classList.contains('moonfin-mediabar-dot') ||
                activeEl.classList.contains('moonfin-focusable') ||
                activeEl.classList.contains('moonfin-focused') ||
                activeEl.classList.contains('moonfin-details-btn') ||
                activeEl.classList.contains('moonfin-details-close') ||
                activeEl.tagName === 'BODY'
            );
            
            const isInMediabar = activeEl && activeEl.closest('.moonfin-mediabar');
            
            if (isInMediabar) {
                if (this.matchKey(key, this.KEYS.LEFT)) {
                    e.preventDefault();
                    e.stopPropagation();
                    if (typeof MediaBar !== 'undefined' && MediaBar.prevSlide) {
                        MediaBar.prevSlide();
                    }
                    return;
                } else if (this.matchKey(key, this.KEYS.RIGHT)) {
                    e.preventDefault();
                    e.stopPropagation();
                    if (typeof MediaBar !== 'undefined' && MediaBar.nextSlide) {
                        MediaBar.nextSlide();
                    }
                    return;
                } else if (this.matchKey(key, this.KEYS.DOWN)) {
                    // From mediabar, go to Jellyfin content
                    e.preventDefault();
                    e.stopPropagation();
                    this.focusJellyfinContent();
                    return;
                } else if (this.matchKey(key, this.KEYS.UP)) {
                    // From mediabar, go to navbar
                    e.preventDefault();
                    e.stopPropagation();
                    const homeBtn = document.querySelector('.moonfin-navbar .moonfin-nav-home');
                    if (homeBtn) {
                        this.focusElement(homeBtn);
                    }
                    return;
                } else if (this.matchKey(key, this.KEYS.ENTER)) {
                    e.preventDefault();
                    e.stopPropagation();
                    if (typeof MediaBar !== 'undefined' && MediaBar.items && MediaBar.items[MediaBar.currentIndex]) {
                        const item = MediaBar.items[MediaBar.currentIndex];
                        if (typeof Details !== 'undefined') {
                            Details.showDetails(item.Id, item.Type);
                        } else {
                            API.navigateToItem(item.Id);
                        }
                    }
                    return;
                }
            }
            
            if (!isMoonfinElement) return;
            
            if (this.matchKey(key, this.KEYS.LEFT)) {
                e.preventDefault();
                e.stopPropagation();
                this.navigate('left');
            } else if (this.matchKey(key, this.KEYS.RIGHT)) {
                e.preventDefault();
                e.stopPropagation();
                this.navigate('right');
            } else if (this.matchKey(key, this.KEYS.UP)) {
                e.preventDefault();
                e.stopPropagation();
                this.navigate('up');
            } else if (this.matchKey(key, this.KEYS.DOWN)) {
                e.preventDefault();
                e.stopPropagation();
                this.navigate('down');
            } else if (this.matchKey(key, this.KEYS.ENTER)) {
                e.preventDefault();
                e.stopPropagation();
                this.activateFocused();
            } else if (this.matchKey(key, this.KEYS.BACK)) {
                this.handleBack(e);
            }
        }, true); // <-- capture phase
    },

    matchKey(key, keyArray) {
        return keyArray.includes(key) || keyArray.includes(parseInt(key));
    },

    setupMutationObserver() {
        var self = this;
        var debounceTimer = null;
        const observer = new MutationObserver(() => {
            if (debounceTimer) clearTimeout(debounceTimer);
            debounceTimer = setTimeout(() => {
                self.updateFocusableElements();
            }, 150);
        });
        
        observer.observe(document.body, {
            childList: true,
            subtree: true
        });
    },

    updateFocusableElements() {
        this.focusableElements = Array.from(
            document.querySelectorAll(this.focusableSelector)
        ).filter(el => {

            const style = window.getComputedStyle(el);
            return style.display !== 'none' && 
                   style.visibility !== 'hidden' &&
                   !el.disabled &&
                   !el.classList.contains('hidden');
        });
    },

    navigate(direction) {
        this.updateFocusableElements();
        
        const currentFocused = document.activeElement;
        const currentIndex = this.focusableElements.indexOf(currentFocused);
        
        if (direction === 'down' && this.isInNavbar(currentFocused)) {
            if (this.focusMediabar()) {
                return;
            }
            // Otherwise hand off focus to Jellyfin content below
            if (this.focusJellyfinContent()) {
                return;
            }
        }
        
        if (direction === 'up' && !this.isInNavbar(currentFocused) && !this.isInMediabar(currentFocused)) {
            if (this.focusMediabar()) {
                return;
            }
            const navbar = document.querySelector('.moonfin-navbar');
            if (navbar) {
                const navbarRect = navbar.getBoundingClientRect();
                const currentRect = currentFocused.getBoundingClientRect();
                
                // If we're near the top, try to focus navbar
                if (currentRect.top < navbarRect.bottom + 200) {
                    const homeBtn = navbar.querySelector('.moonfin-nav-home');
                    if (homeBtn) {
                        this.focusElement(homeBtn);
                        return;
                    }
                }
            }
        }
        
        if (this.focusableElements.length === 0) return;
        
        let nextElement = null;
        
        if (currentIndex === -1) {
            nextElement = this.focusableElements[0];
        } else {
            nextElement = this.findNextElement(currentFocused, direction);
        }
        
        if (nextElement) {
            this.focusElement(nextElement);
        } else if (direction === 'down') {
            // No moonfin element found below, try Jellyfin content
            this.focusJellyfinContent();
        }
    },
    
    isInNavbar(element) {
        return element && element.closest('.moonfin-navbar') !== null;
    },
    
    isInMediabar(element) {
        return element && element.closest('.moonfin-mediabar') !== null;
    },
    
    focusMediabar() {
        const mediabar = document.querySelector('.moonfin-mediabar');
        if (!mediabar) return false;
        
        const style = window.getComputedStyle(mediabar);
        if (style.display === 'none' || style.visibility === 'hidden') {
            return false;
        }
        
        if (typeof MediaBar === 'undefined' || !MediaBar.items || MediaBar.items.length === 0) {
            return false;
        }
        
        // Focus the mediabar content area (make it focusable)
        const content = mediabar.querySelector('.moonfin-mediabar-content');
        if (content) {
            content.setAttribute('tabindex', '0');
            content.focus();
            content.classList.add('moonfin-focused');
            console.log('[Moonfin TV] Focused mediabar');
            return true;
        }
        
        return false;
    },
    
    focusJellyfinContent() {
        // Jellyfin uses these selectors for focusable content
        const jellyfinSelectors = [
            '.card',
            '.listItem',
            '.emby-button',
            '.emby-tab-button',
            '.itemsContainer button',
            '.section0 .card',
            '.homeSection .card',
            '[data-action]',
            '.button-flat',
            '.raised'
        ];
        
        const navbar = document.querySelector('.moonfin-navbar');
        const mediabar = document.querySelector('.moonfin-mediabar');
        
        let topOffset = 0;
        if (navbar) {
            topOffset = Math.max(topOffset, navbar.getBoundingClientRect().bottom);
        }
        if (mediabar && window.getComputedStyle(mediabar).display !== 'none') {
            topOffset = Math.max(topOffset, mediabar.getBoundingClientRect().bottom);
        }
        
        for (const selector of jellyfinSelectors) {
            const elements = document.querySelectorAll(selector);
            for (const el of elements) {
                const rect = el.getBoundingClientRect();
                // Find first element below our UI that's visible
                if (rect.top > topOffset && rect.width > 0 && rect.height > 0) {
                    el.focus();
                    el.classList.add('moonfin-jf-focused');
                    console.log('[Moonfin TV] Focused Jellyfin element:', el);
                    return true;
                }
            }
        }
        
        return false;
    },

    findNextElement(currentElement, direction) {
        const currentRect = currentElement.getBoundingClientRect();
        const currentCenter = {
            x: currentRect.left + currentRect.width / 2,
            y: currentRect.top + currentRect.height / 2
        };
        
        let candidates = [];
        
        for (const el of this.focusableElements) {
            if (el === currentElement) continue;
            
            const rect = el.getBoundingClientRect();
            const center = {
                x: rect.left + rect.width / 2,
                y: rect.top + rect.height / 2
            };
            
            let isValid = false;
            let distance = Infinity;
            
            switch (direction) {
                case 'left':
                    isValid = center.x < currentCenter.x;
                    distance = this.calculateDistance(currentCenter, center, 'horizontal');
                    break;
                case 'right':
                    isValid = center.x > currentCenter.x;
                    distance = this.calculateDistance(currentCenter, center, 'horizontal');
                    break;
                case 'up':
                    isValid = center.y < currentCenter.y;
                    distance = this.calculateDistance(currentCenter, center, 'vertical');
                    break;
                case 'down':
                    isValid = center.y > currentCenter.y;
                    distance = this.calculateDistance(currentCenter, center, 'vertical');
                    break;
            }
            
            if (isValid) {
                candidates.push({ element: el, distance });
            }
        }
        
        candidates.sort((a, b) => a.distance - b.distance);
        return candidates[0]?.element || null;
    },

    calculateDistance(from, to, axis) {
        const dx = to.x - from.x;
        const dy = to.y - from.y;
        
        // Weight the perpendicular axis more heavily to prefer elements in a line
        if (axis === 'horizontal') {
            return Math.abs(dx) + Math.abs(dy) * 2;
        } else {
            return Math.abs(dy) + Math.abs(dx) * 2;
        }
    },

    focusElement(element) {
        this.focusableElements.forEach(el => {
            el.classList.remove('moonfin-focused');
        });
        
        element.classList.add('moonfin-focused');
        element.focus();
        
        element.scrollIntoView({
            behavior: 'smooth',
            block: 'nearest',
            inline: 'nearest'
        });
    },

    activateFocused() {
        const focused = document.activeElement;
        if (focused && this.focusableElements.includes(focused)) {
            focused.click();
        }
    },

    handleBack(e) {
        const settingsPanel = document.querySelector('.moonfin-settings-panel');
        const jellyseerrModal = document.querySelector('.moonfin-jellyseerr-modal');
        
        if (settingsPanel && !settingsPanel.classList.contains('hidden')) {
            e.preventDefault();
            settingsPanel.classList.add('hidden');
            return;
        }
        
        if (jellyseerrModal) {
            e.preventDefault();
            jellyseerrModal.remove();
            return;
        }
        
        // Otherwise, let jellyfin-web handle the back navigation
        // or call NativeShell if available
        if (window.NativeShell?.AppHost?.exit) {
            // Don't prevent default - let the app handle it
        }
    },

    setFocus(selector) {
        this.updateFocusableElements();
        const element = document.querySelector(selector);
        if (element && this.focusableElements.includes(element)) {
            this.focusElement(element);
        }
    },

    focusFirst() {
        this.updateFocusableElements();
        if (this.focusableElements.length > 0) {
            this.focusElement(this.focusableElements[0]);
        }
    },

    addFocusableSelector(selector) {
        this.focusableSelector += `, ${selector}`;
        this.updateFocusableElements();
    },

    disable() {
        this.enabled = false;
    },

    enable() {
        if (this.isTV()) {
            this.enabled = true;
        }
    }
};
