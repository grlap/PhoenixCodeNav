# PhoenixCodeNav website

Static, package-free product website for PhoenixCodeNav. It is designed for people who are new to MCP and large-repository code navigation, while keeping the technical claims aligned with the repository documentation.

## Files

- `index.html` — semantic page content, SEO metadata, product examples, and structured data
- `styles.css` — complete responsive visual system and CSS motion
- `script.js` — hero atlas, scroll reveals, copy buttons, navigation, and accessibility enhancements
- `verify.mjs` — package-free structural, asset-reference integrity, accessibility-reference, and launch-guard checks
- `assets/` — original code-native brand assets

The published page has no package manager, build command, third-party font, CDN, analytics, or external runtime dependency. The repository verifier is a development tool and requires Node.js plus Git working-tree/index metadata (ordinary or split index) so it can prove every referenced CSS and JavaScript asset is tracked; it does not launch the Git executable.

## Local preview

The page works when opened directly with `file://`. For the most representative preview, run a small HTTP server from the repository root:

```powershell
python -m http.server 8080 --directory website
```

Then open `http://localhost:8080/`.

## Verification

Run the package-free source checks from the repository root (with Node.js and Git working-tree metadata available):

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
3. Keep the repository's Apache-2.0 `LICENSE`, `NOTICE`, and third-party notices published with every PhoenixCodeNav source or binary distribution.
4. Confirm the host serves CSS, JavaScript, and SVG with their correct MIME types. This repository deploys through GitHub Pages. GitHub Pages caches each stable URL independently, so warm clients can briefly mix HTML and asset generations until those entries revalidate or expire; this workflow does not configure custom per-asset cache headers. Before upload, the deployment workflow runs `node ./website/verify.mjs --auto`, selecting prelaunch or launch checks from the committed robots directive while verifying source and asset-reference integrity. That gate proves repository coherence, not cross-resource cache atomicity. After deployment, verify the current HTML and both assets with cold requests.

## Accessibility and motion

- All essential content remains available without JavaScript.
- The mobile menu is a native `details` element and gains focus containment when JavaScript runs.
- The hero animation has a visible pause/play control and stops while offscreen or when the page is hidden.
- `prefers-reduced-motion` skips continuous canvas animation, draws the final explanatory state as a still image, and reacts immediately when the preference changes after page load without losing the user's pause choice.
- Code examples wrap instead of creating page-level horizontal overflow.
