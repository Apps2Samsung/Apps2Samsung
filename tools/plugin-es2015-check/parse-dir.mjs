// Parses every .js file under a directory as ES2015 (a classic script, as the TV loads them).
// Used on the PluginPatchRig output: what the real patcher put in plugin_cache, after esbuild.
//
// Usage: node parse-dir.mjs <dir>

import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { parse } from 'acorn';

const [root] = process.argv.slice(2);
if (!root) {
    console.error('usage: node parse-dir.mjs <dir>');
    process.exit(2);
}

const files = [];
const walk = dir => {
    for (const name of readdirSync(dir)) {
        const full = join(dir, name);
        if (statSync(full).isDirectory()) walk(full);
        else if (name.endsWith('.js')) files.push(full);
    }
};
walk(root);

let failures = 0;
for (const file of files) {
    const source = readFileSync(file, 'utf8');
    try {
        parse(source, { ecmaVersion: 2015, sourceType: 'script' });
    } catch (e) {
        const line = source.split('\n')[e.loc.line - 1] ?? '';
        console.log(`✗ ${relative(root, file)}: not ES2015 (${e.message})\n    ${line.trim().slice(0, 160)}`);
        failures++;
    }
}

console.log(failures === 0 ? `All ${files.length} files parse as ES2015.` : `${failures} of ${files.length} files are not ES2015.`);
process.exit(failures === 0 ? 0 : 1);
