const fs = require('fs');
const path = require('path');

const srcDir = path.join(__dirname, 'src');
const distDir = path.join(__dirname, 'dist');
const serverWebDir = path.join(__dirname, '..', 'backend', 'Web');

// Ensure dist directory exists
if (!fs.existsSync(distDir)) {
    fs.mkdirSync(distDir, { recursive: true });
}

// Concatenate all JS files
function buildJS() {
    const jsFiles = [
        'utils/device.js',
        'utils/api.js',
        'utils/storage.js',
        'utils/mdblist.js',
        'utils/tmdb.js',
        'utils/tv-navigation.js',
        'components/navbar.js',
        'components/sidebar.js',
        'components/mediabar.js',
        'components/genres.js',
        'components/library.js',
        'components/settings.js',
        'components/jellyseerr.js',
        'components/details.js',
        'plugin.js'
    ];

    let combinedCode = '';

    for (const file of jsFiles) {
        const filePath = path.join(srcDir, file);
        if (fs.existsSync(filePath)) {
            const content = fs.readFileSync(filePath, 'utf8');
            combinedCode += `// === ${file} ===\n`;
            combinedCode += content + '\n\n';
        } else {
            console.warn(`Warning: ${file} not found`);
        }
    }

    let output = '// Moonfin Web Plugin - Built ' + new Date().toISOString() + '\n';
    output += '(function() {\n';
    output += '"use strict";\n\n';
    output += combinedCode;
    output += '\n})();\n';

    fs.writeFileSync(path.join(distDir, 'plugin.js'), output);
    // Also copy to server Web folder
    if (fs.existsSync(serverWebDir)) {
        fs.writeFileSync(path.join(serverWebDir, 'plugin.js'), output);
    }
    console.log('Built plugin.js');
}

// Copy CSS files
function buildCSS() {
    const cssFiles = [
        'styles/navbar.css',
        'styles/sidebar.css',
        'styles/mediabar.css',
        'styles/mdblist.css',
        'styles/genres.css',
        'styles/settings.css',
        'styles/jellyseerr.css',
        'styles/animations.css',
        'styles/tv-mode.css',
        'styles/details.css'
    ];

    let output = '/* Moonfin Web Plugin CSS - Built ' + new Date().toISOString() + ' */\n\n';

    for (const file of cssFiles) {
        const filePath = path.join(srcDir, file);
        if (fs.existsSync(filePath)) {
            const content = fs.readFileSync(filePath, 'utf8');
            output += `/* === ${file} === */\n`;
            output += content + '\n\n';
        } else {
            console.warn(`Warning: ${file} not found`);
        }
    }

    fs.writeFileSync(path.join(distDir, 'plugin.css'), output);
    // Also copy to server Web folder
    if (fs.existsSync(serverWebDir)) {
        fs.writeFileSync(path.join(serverWebDir, 'plugin.css'), output);
    }
    console.log('Built plugin.css');
}

// Build all
buildJS();
buildCSS();

// Watch mode
if (process.argv.includes('--watch')) {
    console.log('Watching for changes...');
    fs.watch(srcDir, { recursive: true }, (eventType, filename) => {
        console.log(`Change detected: ${filename}`);
        if (filename.endsWith('.js')) {
            buildJS();
        } else if (filename.endsWith('.css')) {
            buildCSS();
        }
    });
}
