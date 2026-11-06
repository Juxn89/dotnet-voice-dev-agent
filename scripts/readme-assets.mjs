// Generates the README artwork in docs/readme/: hero banner, metrics strip, feature icons, the
// divider, and the HTML page the showcase screenshots are taken from. Plain Node, no dependencies:
//
//   node scripts/readme-assets.mjs
//
// GitHub shows README images through <img>: plain SVG, no external fonts or scripts. CSS animations
// inside the SVG do run there, and they stop under prefers-reduced-motion.
//
// The showcase PNGs are screenshots of docs/readme/showcase.html, in both color schemes:
//
//   npx playwright@1.64.1 screenshot --device="Desktop Chrome HiDPI" --viewport-size=1100,600 --full-page \
//     --color-scheme=dark  file:///<repo>/docs/readme/showcase.html docs/readme/showcase-dark.png
//   (and again with --color-scheme=light into showcase-light.png)
import { mkdir, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const out = fileURLToPath(new URL('../docs/readme/', import.meta.url));
await mkdir(out, { recursive: true });

const brand = { rose: '#be123c', blush: '#fb7185', amber: '#f59e0b' };

const themes = {
	dark: { bg: '#0d0a0c', panel: '#171114', card: '#1d1519', border: '#33252b', text: '#f8f1f3', muted: '#a8999f', code: '#fda4af', dot: '#ffffff', dotOpacity: 0.06, orb: 0.3 },
	light: { bg: '#fffafb', panel: '#ffffff', card: '#ffffff', border: '#f0dfe3', text: '#1a0f13', muted: '#5f5157', code: '#9f1239', dot: '#be123c', dotOpacity: 0.07, orb: 0.14 },
};

const sans = `'Segoe UI', Inter, -apple-system, BlinkMacSystemFont, 'Helvetica Neue', Arial, sans-serif`;
const mono = `'Cascadia Code', 'SFMono-Regular', Consolas, Menlo, monospace`;
const escape = (text) => text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

function defs(t) {
	return `
	<linearGradient id="brand" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="${brand.blush}"/><stop offset=".55" stop-color="${brand.rose}"/><stop offset="1" stop-color="${brand.amber}"/></linearGradient>
	<linearGradient id="metric" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="${brand.blush}"/><stop offset="1" stop-color="${brand.amber}"/></linearGradient>
	<radialGradient id="orbRose"><stop offset="0" stop-color="${brand.rose}" stop-opacity="${t.orb}"/><stop offset="1" stop-color="${brand.rose}" stop-opacity="0"/></radialGradient>
	<radialGradient id="orbAmber"><stop offset="0" stop-color="${brand.amber}" stop-opacity="${t.orb * 0.6}"/><stop offset="1" stop-color="${brand.amber}" stop-opacity="0"/></radialGradient>
	<pattern id="dots" width="22" height="22" patternUnits="userSpaceOnUse"><circle cx="2" cy="2" r="1.1" fill="${t.dot}" fill-opacity="${t.dotOpacity}"/></pattern>`;
}

// ── Hero: what it does, drawn — a heard request splits into a short spoken answer and the screen ────
function hero(name) {
	const t = themes[name];
	const W = 1280;
	const H = 460;
	const accent = name === 'dark' ? brand.blush : brand.rose;
	const chips = ['Push-to-talk', 'Local Whisper & Piper', 'Claude Haiku 5.5', 'Memory per repo', 'Read-only tools'];
	let x = 72;
	const chipSvg = chips
		.map((chip) => {
			const w = chip.length * 8.2 + 30;
			const g = `<g transform="translate(${x} 352)"><rect width="${w}" height="32" rx="16" fill="${t.panel}" stroke="${t.border}"/><text x="${w / 2}" y="21" text-anchor="middle" font-family="${sans}" font-size="14" font-weight="600" fill="${t.text}">${escape(chip)}</text></g>`;
			x += w + 8;
			return g;
		})
		.join('');

	const card = (w, h, label, glyph) => `
		<rect width="${w}" height="${h}" rx="16" fill="${t.card}" stroke="${t.border}"/>
		<rect x="14" y="14" width="24" height="24" rx="7" fill="${brand.rose}"/>
		<g transform="translate(18 18) scale(.6667)" fill="none" stroke="#fff" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round">${glyph}</g>
		<text x="48" y="31" font-family="${sans}" font-size="12.5" font-weight="700" fill="${t.text}">${label}</text>`;

	return `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" role="img" aria-labelledby="title desc">
<title id="title">dotnet-voice-dev-agent</title>
<desc id="desc">A .NET dev agent you talk to: it answers in a sentence or two, puts the details on screen, and remembers your repository between sessions. Its tools are read-only.</desc>
<style>
	.flow { stroke-dasharray: 6 7; animation: flow 1.6s linear infinite; }
	.pop { opacity: 0; animation: pop .5s ease-out forwards; }
	.glow { animation: glow 2.4s ease-in-out infinite alternate; }
	@keyframes flow { to { stroke-dashoffset: -26; } }
	@keyframes pop { from { opacity: 0; transform: translateY(6px); } to { opacity: 1; transform: none; } }
	@keyframes glow { from { opacity: .6; } to { opacity: 1; } }
	@media (prefers-reduced-motion: reduce) { .flow, .glow { animation: none; } .pop { animation: none; opacity: 1; } }
</style>
<defs>${defs(t)}
	<clipPath id="frame"><rect width="${W}" height="${H}" rx="28"/></clipPath>
	<filter id="soft" x="-20%" y="-20%" width="140%" height="160%"><feDropShadow dx="0" dy="14" stdDeviation="16" flood-color="#000" flood-opacity="${name === 'dark' ? 0.45 : 0.1}"/></filter>
</defs>
<g clip-path="url(#frame)">
	<rect width="${W}" height="${H}" fill="${t.bg}"/>
	<rect width="${W}" height="${H}" fill="url(#dots)"/>
	<circle cx="180" cy="40" r="340" fill="url(#orbRose)"/>
	<circle cx="1080" cy="480" r="360" fill="url(#orbAmber)"/>
</g>
<rect x=".5" y=".5" width="${W - 1}" height="${H - 1}" rx="28" fill="none" stroke="${t.border}"/>

<g transform="translate(72 70)">
	<rect width="252" height="30" rx="15" fill="${t.panel}" stroke="${t.border}"/>
	<circle cx="18" cy="15" r="4.5" fill="${brand.blush}" class="glow"/>
	<text x="31" y="20" font-family="${sans}" font-size="12.5" font-weight="700" letter-spacing=".1em" fill="${t.muted}">OPEN SOURCE · .NET 10 · MIT</text>
</g>
<text x="68" y="168" font-family="${sans}" font-size="56" font-weight="800" letter-spacing="-.03em" fill="${t.text}">dotnet-<tspan fill="url(#brand)">voice-dev-agent</tspan></text>
<text x="72" y="226" font-family="${sans}" font-size="24" font-weight="600" fill="${t.text}">A .NET dev agent you talk to.</text>
<text x="72" y="264" font-family="${sans}" font-size="20" fill="${t.muted}">Answers in a sentence, puts the details on screen,</text>
<text x="72" y="296" font-family="${sans}" font-size="20" fill="${t.muted}">remembers your repo. Never edits a line of code.</text>
${chipSvg}

<!-- What it heard -->
<g transform="translate(792 62)" filter="url(#soft)">
	${card(196, 104, 'heard', '<path d="M12 2a3 3 0 0 0-3 3v7a3 3 0 0 0 6 0V5a3 3 0 0 0-3-3Z"/><path d="M19 10v2a7 7 0 0 1-14 0v-2"/><line x1="12" x2="12" y1="19" y2="22"/>')}
	<text font-family="${mono}" font-size="12" fill="${t.code}"><tspan x="16" y="68">"why is the build</tspan><tspan x="16" y="86">red?"</tspan></text>
</g>

<!-- What it remembered from last time -->
<g transform="translate(792 202)" filter="url(#soft)">
	${card(196, 120, 'memory', '<rect width="16" height="20" x="4" y="2" rx="2"/><path d="M2 6h4"/><path d="M2 10h4"/><path d="M2 14h4"/><path d="M2 18h4"/><path d="M16 2v20"/>')}
	<text font-family="${sans}" font-size="12" fill="${t.muted}"><tspan x="16" y="66">Core is the project</tspan><tspan x="16" y="84">that matters. Finish the</tspan><tspan x="16" y="102">rename on Monday.</tspan></text>
</g>
<path d="M890 202 L 890 166" fill="none" stroke="${accent}" stroke-width="2.2" class="flow"/>

<!-- One sentence to the ear -->
<g transform="translate(1034 54)" filter="url(#soft)"><g class="pop" style="animation-delay:.3s">
	${card(202, 120, 'says', '<path d="M11 4.702a.705.705 0 0 0-1.203-.498L6.413 7.587A1.4 1.4 0 0 1 5.416 8H3a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2.416a1.4 1.4 0 0 1 .997.413l3.383 3.384A.705.705 0 0 0 11 19.298z"/><path d="M16 9a5 5 0 0 1 0 6"/><path d="M19.364 18.364a9 9 0 0 0 0-12.728"/>')}
	<text font-family="${sans}" font-size="12.5" fill="${t.text}"><tspan x="16" y="66">"Build's red, one error,</tspan><tspan x="16" y="84">left from your rename.</tspan><tspan x="16" y="102">Details on screen."</tspan></text>
</g></g>

<!-- The rest to the screen -->
<g transform="translate(1034 196)" filter="url(#soft)"><g class="pop" style="animation-delay:.9s">
	${card(202, 126, 'screen', '<rect width="20" height="14" x="2" y="3" rx="2"/><line x1="8" x2="16" y1="21" y2="21"/><line x1="12" x2="12" y1="17" y2="21"/>')}
	<text font-family="${mono}" font-size="11" fill="${t.code}"><tspan x="16" y="66">ReviewOrchestrator.cs</tspan><tspan x="16" y="84">(111,54): CS1061</tspan><tspan x="16" y="102" fill="${t.muted}">no member 'Patch'</tspan></text>
</g></g>

<path d="M988 108 C 1012 108, 1010 108, 1034 108" fill="none" stroke="${accent}" stroke-width="2.2" class="flow"/>
<path d="M988 130 C 1014 130, 1008 252, 1034 252" fill="none" stroke="${accent}" stroke-width="2.2" class="flow"/>
<circle cx="988" cy="108" r="4.5" fill="${accent}"/>
<circle cx="988" cy="130" r="4.5" fill="${accent}"/>
</svg>
`;
}

// ── Metrics: each one traceable to a measurement in the articles or to the test run ───────────────
const metricItems = [
	['7 s', 'to listen to an answer', 'was 57–68 s with a prompt for the screen'],
	['4 / 4', 'Mondays that recalled Friday', 'memory preloaded · Claude Haiku 5.5'],
	['< $0.003', 'for a morning of questions', 'three questions, tools and memory included'],
	['103', 'tests, no audio or API key', 'xUnit · no Docker, no network'],
];
const metricsAlt = '7 seconds to listen to an answer, down from 57 to 68 with a prompt written for the screen. 4 of 4 Monday sessions recalled what was left from Friday, with memory preloaded, on Claude Haiku 5.5. Under 0.003 US dollars for a morning of three questions, tools and memory included. 103 tests that need no audio device, API key, Docker or network.';

function metrics(name) {
	const t = themes[name];
	const W = 1280;
	const H = 150;
	const col = W / metricItems.length;
	const cells = metricItems
		.map(
			([value, label, note], i) => `
	<g transform="translate(${col * i + col / 2} 0)">
		<text y="64" text-anchor="middle" font-family="${sans}" font-size="40" font-weight="800" letter-spacing="-.02em" fill="url(#metric)">${escape(value)}</text>
		<text y="94" text-anchor="middle" font-family="${sans}" font-size="16" font-weight="600" fill="${t.text}">${escape(label)}</text>
		<text y="116" text-anchor="middle" font-family="${sans}" font-size="13.5" fill="${t.muted}">${escape(note)}</text>
	</g>${i > 0 ? `<line x1="${col * i}" y1="36" x2="${col * i}" y2="114" stroke="${t.border}"/>` : ''}`,
		)
		.join('');
	return `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" role="img" aria-label="${metricsAlt}">
<defs>${defs(t)}</defs>
<rect x=".5" y=".5" width="${W - 1}" height="${H - 1}" rx="22" fill="${t.panel}" stroke="${t.border}"/>
${cells}
</svg>
`;
}

// ── Feature icons: Lucide glyphs (ISC license) on a gradient tile ──────────────────────────────────
const glyphs = {
	mic: '<path d="M12 2a3 3 0 0 0-3 3v7a3 3 0 0 0 6 0V5a3 3 0 0 0-3-3Z"/><path d="M19 10v2a7 7 0 0 1-14 0v-2"/><line x1="12" x2="12" y1="19" y2="22"/>',
	transcript: '<path d="M2 10v3"/><path d="M6 6v11"/><path d="M10 3v18"/><path d="M14 8v7"/><path d="M18 5v13"/><path d="M22 10v3"/>',
	speaker: '<path d="M11 4.702a.705.705 0 0 0-1.203-.498L6.413 7.587A1.4 1.4 0 0 1 5.416 8H3a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2.416a1.4 1.4 0 0 1 .997.413l3.383 3.384A.705.705 0 0 0 11 19.298z"/><path d="M16 9a5 5 0 0 1 0 6"/><path d="M19.364 18.364a9 9 0 0 0 0-12.728"/>',
	screen: '<rect width="20" height="14" x="2" y="3" rx="2"/><line x1="8" x2="16" y1="21" y2="21"/><line x1="12" x2="12" y1="17" y2="21"/>',
	memory: '<rect width="16" height="20" x="4" y="2" rx="2"/><path d="M2 6h4"/><path d="M2 10h4"/><path d="M2 14h4"/><path d="M2 18h4"/><path d="M16 2v20"/>',
	verify: '<path d="M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z"/><path d="m9 12 2 2 4-4"/>',
	read: '<path d="M14 2v4a2 2 0 0 0 2 2h4"/><path d="M4.268 21a2 2 0 0 0 1.727 1H18a2 2 0 0 0 2-2V7l-5-5H6a2 2 0 0 0-2 2v3"/><path d="m9 18-1.5-1.5"/><circle cx="5" cy="14" r="3"/>',
	lock: '<rect width="18" height="11" x="3" y="11" rx="2" ry="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/>',
	tests: '<path d="M14 2v6a2 2 0 0 0 .245.96l5.51 10.08A2 2 0 0 1 18 22H6a2 2 0 0 1-1.755-2.96l5.51-10.08A2 2 0 0 0 10 8V2"/><path d="M6.453 15h11.094"/><path d="M8.5 2h7"/>',
};

const tiles = {
	mic: ['#9f1239', '#fb7185'],
	transcript: ['#0f766e', '#2dd4bf'],
	speaker: ['#be185d', '#f472b6'],
	screen: ['#1e3a8a', '#60a5fa'],
	memory: ['#b45309', '#fbbf24'],
	verify: ['#065f46', '#10b981'],
	read: ['#4338ca', '#818cf8'],
	lock: ['#334155', '#64748b'],
	tests: ['#7c2d12', '#f97316'],
};

function iconSvg(name) {
	const [from, to] = tiles[name];
	return `<svg xmlns="http://www.w3.org/2000/svg" width="56" height="56" viewBox="0 0 56 56" aria-hidden="true">
<defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="${from}"/><stop offset="1" stop-color="${to}"/></linearGradient></defs>
<rect width="56" height="56" rx="16" fill="url(#g)"/>
<rect x=".75" y=".75" width="54.5" height="54.5" rx="15.25" fill="none" stroke="#fff" stroke-opacity=".18" stroke-width="1.5"/>
<g transform="translate(16 16)" fill="none" stroke="#fff" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">${glyphs[name]}</g>
</svg>
`;
}

const divider = `<svg xmlns="http://www.w3.org/2000/svg" width="1280" height="24" viewBox="0 0 1280 24" aria-hidden="true">
<defs><linearGradient id="d" x1="0" x2="1"><stop offset="0" stop-color="${brand.blush}" stop-opacity="0"/><stop offset=".3" stop-color="${brand.rose}"/><stop offset=".7" stop-color="${brand.amber}"/><stop offset="1" stop-color="${brand.amber}" stop-opacity="0"/></linearGradient></defs>
<rect y="11" width="1280" height="2" rx="1" fill="url(#d)"/>
<rect x="632" y="4" width="16" height="16" rx="5" fill="${brand.rose}"/><path d="M636.5 12v0M640 9.5v5M643.5 12v0" fill="none" stroke="#fff" stroke-width="2" stroke-linecap="round"/>
</svg>
`;

// ── Showcase: a real Monday session, in the app's own console format ─────────────────────────────
// Verbatim from the Part 3 measurements (session "monday-M2-13": memory preloaded, Claude Haiku 5.5,
// typed requests against claude-pr-review-bot with a half-finished rename). Requests, everything the
// agent put on screen, the spoken reply and the timing are copied from that run's capture; only the
// layout is the app's. Nothing here is written by hand.
const session = [
	{
		request: 'What was I doing on Friday?',
		screen: [
			'Branch: rename-patch-to-diff (uncommitted, 4 files)\n- src/ClaudeReviewBot.Core/Diff/ChangedFile.cs\n- src/ClaudeReviewBot.Core/Diff/PatchParser.cs\n- src/ClaudeReviewBot.Core/Prompts/DiffRenderer.cs\n- tests/ClaudeReviewBot.Tests/PatchParserTests.cs\nStill open: src/ClaudeReviewBot.Core/Review/ReviewOrchestrator.cs line 111 uses file.Patch.',
			'On Friday you were renaming ChangedFile.Patch to Diff on the rename-patch-to-diff branch, across four uncommitted files. The one leftover is line 111 of the review orchestrator, which still uses Patch; the list is on screen.',
		],
		ms: 6897,
		says: 'On Friday you were renaming ChangedFile.Patch to Diff on the rename-patch-to-diff branch, across four uncommitted files. The rest is on screen.',
	},
	{
		request: 'Build the project I care about.',
		screen: ["Core build: 1 error, 0 warnings\nsrc/ClaudeReviewBot.Core/Review/ReviewOrchestrator.cs(111,54): CS1061: 'ChangedFile' does not contain a definition for 'Patch'"],
		ms: 5107,
		says: 'Details are on screen.',
	},
	{
		request: "What's left to finish the rename?",
		screen: ["The only thing left is line 111 of the review orchestrator, which still reads Patch; it's the one error in the whole solution build. Changing it to Diff should clear the build, but I haven't run the tests yet to confirm nothing else depends on the old name."],
		ms: 5467,
		says: "The only thing left is line 111 of the review orchestrator, which still reads Patch; it's the one error in the whole solution build. The rest is on screen.",
	},
];

function showcaseHtml() {
	const box = (text) => `<span class="dim">  ┌ screen</span>\n${text
		.split('\n')
		.map((line) => `<span class="dim">  │ </span><span class="screen">${escape(line)}</span>`)
		.join('\n')}\n<span class="dim">  └</span>`;
	const turns = session
		.map(
			(turn) => `\n<span class="prompt">&gt; </span><span class="you">${escape(turn.request)}</span>
${turn.screen.map(box).join('\n')}
<span class="dim">  (${turn.ms} ms)</span>
<span class="dim">  says: </span><span class="says">"${escape(turn.says)}"</span>`,
		)
		.join('\n');
	return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>dotnet-voice-dev-agent: a Monday session</title>
<style>
	:root { --bg: #fffafb; --win: #ffffff; --bar: #f6eaee; --border: #f0dfe3; --text: #1a0f13; --dim: #8a7a80; --you: #1a0f13; --screen: #9f1239; --says: #b45309; }
	@media (prefers-color-scheme: dark) { :root { --bg: #0d0a0c; --win: #171114; --bar: #1f171b; --border: #33252b; --text: #f8f1f3; --dim: #8f8086; --you: #ffffff; --screen: #fda4af; --says: #fbbf24; } }
	body { margin: 0; padding: 40px; background: var(--bg); font-family: 'Cascadia Code', 'SFMono-Regular', Consolas, Menlo, monospace; }
	.win { max-width: 1020px; margin: 0 auto; background: var(--win); border: 1px solid var(--border); border-radius: 14px; overflow: hidden; box-shadow: 0 24px 48px rgba(0,0,0,.18); }
	.bar { display: flex; align-items: center; gap: 8px; padding: 12px 16px; background: var(--bar); border-bottom: 1px solid var(--border); font-family: 'Segoe UI', Inter, -apple-system, Arial, sans-serif; font-size: 13px; color: var(--dim); }
	.bar i { width: 12px; height: 12px; border-radius: 50%; display: inline-block; }
	.bar span { margin-left: 10px; }
	pre { margin: 0; padding: 18px 22px 24px; font: 14.5px/1.6 inherit; font-family: inherit; color: var(--text); white-space: pre-wrap; word-break: break-word; }
	.dim { color: var(--dim); } .prompt { color: var(--says); font-weight: 700; } .you { color: var(--you); font-weight: 700; }
	.screen { color: var(--screen); } .says { color: var(--says); }
</style>
</head>
<body>
<div class="win">
	<div class="bar"><i style="background:#ff5f57"></i><i style="background:#febc2e"></i><i style="background:#28c840"></i><span>dotnet run --project src/VoiceDevAgent -- R:\\ --text</span></div>
<pre><span class="dim">Memory loaded for this repository.
Type a request, empty line to quit.</span>
${turns}
</pre>
</div>
</body>
</html>
`;
}

for (const name of ['dark', 'light']) {
	await writeFile(`${out}hero-${name}.svg`, hero(name));
	await writeFile(`${out}metrics-${name}.svg`, metrics(name));
}

for (const name of Object.keys(glyphs)) {
	await writeFile(`${out}icon-${name}.svg`, iconSvg(name));
}

await writeFile(`${out}divider.svg`, divider);
await writeFile(`${out}showcase.html`, showcaseHtml());
console.log(`README artwork written to ${out}`);
