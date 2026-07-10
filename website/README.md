# PhoenixCodeNav website

Static, dependency-free product website for PhoenixCodeNav. It is designed for people who are new to MCP and large-repository code navigation, while keeping the technical claims aligned with the repository documentation.

## Files

- `index.html` — semantic page content, SEO metadata, setup examples, and structured data
- `styles.c5bd8d3532.css` — complete responsive visual system and CSS motion
- `script.1f28c1e1e6.js` — hero atlas, scroll reveals, tabs, copy buttons, navigation, and accessibility enhancements
- `verify.mjs` — dependency-free structural, asset-integrity, accessibility-reference, and launch-guard checks
- `assets/` — original code-native brand assets

There is no package manager, build command, third-party font, CDN, analytics, or external runtime dependency.

## Local preview

The page works when opened directly with `file://`. For the most representative preview, run a small HTTP server from the repository root:

```powershell
python -m http.server 8080 --directory website
```

Then open `http://localhost:8080/`.

## Verification

Run the dependency-free source checks from the repository root:

```powershell
node website/verify.mjs
```

That command validates the current prelaunch state. Once licensing and production metadata are in place, run the stricter launch gate:

```powershell
node website/verify.mjs --launch
```

## Deployment

Deploy **only the contents of `website/`** to a static host. Do not publish the repository root, `artifacts/`, `.beads/`, or development configuration.

No production URL is currently configured, so the page intentionally omits a canonical URL, sitemap, `CNAME`, and absolute Open Graph image URL. It also ships with `noindex,nofollow`; change that only when the hosting destination and launch metadata are ready.

Before a public deployment:

1. Choose the hosting URL, update canonical/social metadata, and change the robots directive to `index,follow` at launch.
2. Reconfirm product claims against the intended public commit.
3. Publish a license or explicit use terms before making the website public; repository visibility alone does not grant reuse rights.
4. Confirm the host serves CSS, JavaScript, and SVG with their correct MIME types. The current asset filenames already contain content hashes for cache safety.

## Accessibility and motion

- All essential content remains available without JavaScript.
- The mobile menu is a native `details` element and gains focus containment when JavaScript runs.
- The hero animation has a visible pause/play control and stops while offscreen or when the page is hidden.
- `prefers-reduced-motion` disables canvas and continuous animation while preserving the final explanatory state.
- Configuration tabs support arrow, Home, and End keys.
- Code examples wrap instead of creating page-level horizontal overflow.
