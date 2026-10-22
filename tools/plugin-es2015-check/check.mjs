// Transpiles every upstream plugin file that Apps2Samsung copies into the app with the esbuild
// binary the desktop app ships, and checks that the output parses as ES2015. A plugin update that
// esbuild can't bring down to ES2015 (or that esbuild rejects outright) then fails here, before a
// user installs it onto a TV that can't parse it (#801).
//
// Usage: node check.mjs <esbuild binary> [work dir]

import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { parse } from 'acorn';

const [esbuild, workDir = 'plugin-sources'] = process.argv.slice(2);
if (!esbuild) {
    console.error('usage: node check.mjs <esbuild binary> [work dir]');
    process.exit(2);
}

const { plugins } = JSON.parse(readFileSync(new URL('./sources.json', import.meta.url), 'utf8'));
const git = (args, cwd) => execFileSync('git', args, { cwd, encoding: 'utf8' }).trim();

function checkout(plugin) {
    const dir = join(workDir, plugin.repo.replace('/', '__'));
    const url = `https://github.com/${plugin.repo}`;
    let ref = plugin.ref;
    if (ref === 'latest-tag') {
        // KefinTweaks users point their root at a release tag, so test the newest one, not main.
        const tags = git(['ls-remote', '--tags', '--refs', '--sort=-v:refname', url]).split('\n');
        ref = tags[0]?.split('refs/tags/')[1];
    }
    if (!existsSync(dir)) {
        mkdirSync(workDir, { recursive: true });
        git(['clone', '--quiet', '--depth', '1', ...(ref ? ['--branch', ref] : []), url, dir]);
    }
    return { dir, label: `${plugin.repo}@${ref ?? git(['rev-parse', '--short', 'HEAD'], dir)}` };
}

// "**/" matches any number of directories, "*" anything within one path segment.
function globToRegex(glob) {
    const body = glob
        .split('**/')
        .map(part => part.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '[^/]*'))
        .join('(?:.*/)?');
    return new RegExp(`^${body}$`);
}

function listFiles(root) {
    const out = [];
    const walk = dir => {
        for (const name of readdirSync(dir)) {
            if (name === '.git' || name === 'node_modules') continue;
            const full = join(dir, name);
            if (statSync(full).isDirectory()) walk(full);
            else out.push(relative(root, full).split(sep).join('/'));
        }
    };
    walk(root);
    return out;
}

let failures = 0;
let warnings = 0;
for (const plugin of plugins) {
    const { dir, label } = checkout(plugin);
    const patterns = plugin.files.map(globToRegex);
    const files = listFiles(dir).filter(f => patterns.some(p => p.test(f))).sort();
    console.log(`\n${plugin.name} (${label}): ${files.length} files`);
    if (files.length === 0) {
        console.log('  ✗ no files matched: the repo layout changed, update sources.json');
        failures++;
        continue;
    }

    for (const file of files) {
        let output;
        try {
            // Same target as EsbuildHelper.TranspileAsync, reading stdin instead of a temp file.
            // Target warnings (e.g. BigInt, which ES2015 can only fake with a runtime call) are shown
            // but don't fail the check: the app ships those files too. esbuild's lint warnings about
            // the plugin's own code (duplicate keys and the like) are left out.
            const run = spawnSync(esbuild, ['--target=es2015', '--log-level=warning', '--color=false'], {
                input: readFileSync(join(dir, file)),
                encoding: 'utf8',
                maxBuffer: 64 * 1024 * 1024,
            });
            if (run.error || run.status !== 0) throw Object.assign(run.error ?? new Error(`exit ${run.status}`), { stderr: run.stderr });
            if (run.stderr.includes('configured target environment')) {
                console.log(`  ⚠ ${file}: esbuild warning\n${run.stderr.trim().replace(/^/gm, '      ')}`);
                warnings++;
            }
            output = run.stdout;
        } catch (e) {
            const detail = (e.stderr || e.message).trim().replace(/^/gm, '      ');
            console.log(`  ✗ ${file}: esbuild failed\n${detail}`);
            failures++;
            continue;
        }
        try {
            parse(output, { ecmaVersion: 2015, sourceType: 'script' });
        } catch (e) {
            const line = output.split('\n')[e.loc.line - 1] ?? '';
            console.log(`  ✗ ${file}: transpiled output is not ES2015 (${e.message})\n      ${line.trim().slice(0, 160)}`);
            failures++;
        }
    }
}

console.log(failures === 0
    ? `\nAll plugin files transpile to ES2015 (${warnings} esbuild warning(s)).`
    : `\n${failures} problem(s), ${warnings} esbuild warning(s).`);
process.exit(failures === 0 ? 0 : 1);
