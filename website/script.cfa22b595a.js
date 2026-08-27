(() => {
  "use strict";

  const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
  const finePointer = window.matchMedia("(hover: hover) and (pointer: fine)");
  let animationsPausedByControl = [];

  function setGlobalMotionPaused(paused) {
    const root = document.documentElement;
    if (root.classList.contains("motion-paused") === paused) return;

    if (paused) {
      animationsPausedByControl = document.getAnimations().filter((animation) => animation.playState === "running");
      root.classList.add("motion-paused");
      animationsPausedByControl.forEach((animation) => animation.pause());
      return;
    }

    root.classList.remove("motion-paused");
    const animationsToResume = animationsPausedByControl;
    animationsPausedByControl = [];
    animationsToResume.forEach((animation) => {
      if (animation.playState === "paused") animation.play();
    });
  }

  function setupMenu() {
    const menu = document.getElementById("mobile-menu");
    if (!menu) return;

    const summary = menu.querySelector("summary");
    const pageRegions = [document.querySelector("main"), document.querySelector("footer")].filter(Boolean);
    const desktopViewport = window.matchMedia("(min-width: 1021px)");

    menu.addEventListener("toggle", () => {
      document.body.classList.toggle("menu-open", menu.open);
      pageRegions.forEach((region) => { region.inert = menu.open; });
    });

    menu.querySelectorAll("a").forEach((link) => {
      link.addEventListener("click", () => menu.removeAttribute("open"));
    });
    document.querySelector(".brand")?.addEventListener("click", () => menu.removeAttribute("open"));

    document.addEventListener("keydown", (event) => {
      if (!menu.open) return;

      if (event.key === "Escape") {
        menu.removeAttribute("open");
        summary?.focus();
        return;
      }

      if (event.key !== "Tab") return;
      const focusable = [summary, ...menu.querySelectorAll(".mobile-menu__panel a")].filter(Boolean);
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    });

    desktopViewport.addEventListener("change", (event) => {
      if (event.matches && menu.open) menu.removeAttribute("open");
    });
  }

  function animateHero() {
    if (reducedMotion.matches) return;

    document.querySelectorAll(".hero__line > span").forEach((line, index) => {
      line.animate(
        [
          { opacity: 0, transform: "translateY(112%) rotate(2deg)" },
          { opacity: 1, transform: "translateY(0) rotate(0deg)" }
        ],
        {
          duration: 1050,
          delay: 90 + index * 120,
          easing: "cubic-bezier(.16,1,.3,1)",
          fill: "both"
        }
      );
    });

    [
      [".hero__eyebrow", 100],
      [".hero__lead", 390],
      [".hero__actions", 500],
      [".hero__plain-language", 640],
      [".atlas", 330]
    ].forEach(([selector, delay]) => {
      const element = document.querySelector(selector);
      if (!element) return;
      element.animate(
        [
          { opacity: 0, transform: selector === ".atlas" ? "translateY(28px) scale(.95)" : "translateY(22px)" },
          { opacity: 1, transform: "translateY(0) scale(1)" }
        ],
        {
          duration: 900,
          delay,
          easing: "cubic-bezier(.16,1,.3,1)",
          fill: selector === ".atlas" ? "backwards" : "both"
        }
      );
    });
  }

  function setupReveals() {
    if (reducedMotion.matches || !("IntersectionObserver" in window)) return;

    const observer = new IntersectionObserver((entries) => {
      entries.forEach((entry) => {
        if (!entry.isIntersecting) return;
        const animation = entry.target.__phoenixReveal;
        if (document.documentElement.classList.contains("motion-paused")) animation?.finish();
        else animation?.play();
        observer.unobserve(entry.target);
      });
    }, { threshold: 0.12, rootMargin: "0px 0px -7%" });

    document.querySelectorAll("[data-reveal]").forEach((element, index) => {
      const animation = element.animate(
        [
          { opacity: 0, transform: "translateY(32px)" },
          { opacity: 1, transform: "translateY(0)" }
        ],
        {
          duration: 820,
          delay: (index % 3) * 42,
          easing: "cubic-bezier(.16,1,.3,1)",
          fill: "both"
        }
      );
      animation.pause();
      animation.addEventListener("finish", () => animation.cancel(), { once: true });
      element.__phoenixReveal = animation;
      observer.observe(element);
    });
  }

  function setupActiveNavigation() {
    if (!("IntersectionObserver" in window)) return;
    const links = [...document.querySelectorAll('.nav__links a[href^="#"]')];
    const targets = links.map((link) => document.querySelector(link.getAttribute("href"))).filter(Boolean);
    const observer = new IntersectionObserver((entries) => {
      entries.forEach((entry) => {
        if (!entry.isIntersecting) return;
        links.forEach((link) => {
          if (link.getAttribute("href") === `#${entry.target.id}`) link.setAttribute("aria-current", "true");
          else link.removeAttribute("aria-current");
        });
      });
    }, { rootMargin: "-38% 0px -55%", threshold: 0 });
    targets.forEach((target) => observer.observe(target));
  }

  function setupScrollEffects() {
    const header = document.getElementById("site-header");
    const progress = document.getElementById("scroll-progress-bar");
    let frame = 0;

    const update = () => {
      frame = 0;
      const top = window.scrollY || document.documentElement.scrollTop;
      const scrollable = Math.max(1, document.documentElement.scrollHeight - window.innerHeight);
      header?.classList.toggle("is-scrolled", top > 24);
      if (progress) progress.style.transform = `scaleX(${Math.min(1, top / scrollable)})`;
    };

    const requestUpdate = () => {
      if (!frame) frame = requestAnimationFrame(update);
    };
    window.addEventListener("scroll", requestUpdate, { passive: true });
    window.addEventListener("resize", requestUpdate, { passive: true });
    update();
  }

  function mulberry32(seed) {
    return () => {
      let value = seed += 0x6D2B79F5;
      value = Math.imul(value ^ value >>> 15, value | 1);
      value ^= value + Math.imul(value ^ value >>> 7, value | 61);
      return ((value ^ value >>> 14) >>> 0) / 4294967296;
    };
  }

  function setupAtlas() {
    const atlas = document.getElementById("atlas");
    const scene = document.getElementById("atlas-scene");
    const canvas = document.getElementById("atlas-canvas");
    const context = canvas?.getContext("2d");
    const pauseButton = document.getElementById("atlas-pause");
    const pauseLabel = pauseButton?.querySelector(".atlas__pause-label");
    if (!atlas || !scene || !canvas || !context || !pauseButton || !pauseLabel) return;

    let state = reducedMotion.matches ? 3 : 0;
    // Keep explicit user intent separate from the operating-system preference so a live
    // preference change can suspend motion temporarily without losing the user's pause choice.
    let userPaused = false;
    let visible = true;
    let width = 1;
    let height = 1;
    let dpr = 1;
    let raf = 0;
    let timer = 0;
    let lastTime = performance.now();

    const TAU = Math.PI * 2;
    let stars = [];
    let disk = [];

    function createField() {
      const random = mulberry32(20260821);
      const starCount = width < 520 ? 70 : 110;
      stars = Array.from({ length: starCount }, () => ({
        x: random(),
        y: random(),
        size: 0.4 + random() * 1.0,
        twinkle: random() * TAU,
        warm: random() > 0.8
      }));
      const diskCount = width < 520 ? 520 : 920;
      disk = Array.from({ length: diskCount }, () => {
        const band = Math.pow(random(), 1.5);
        return {
          a: 1.35 + band * 1.25,
          phi: random() * TAU,
          size: 0.4 + random() * 0.9,
          drift: (random() - 0.5) * 0.05,
          heat: 0.5 + random() * 0.5
        };
      });
    }

    function resize() {
      const rect = canvas.getBoundingClientRect();
      width = Math.max(1, rect.width);
      height = Math.max(1, rect.height);
      dpr = Math.min(window.devicePixelRatio || 1, 1.5);
      canvas.width = Math.round(width * dpr);
      canvas.height = Math.round(height * dpr);
      context.setTransform(dpr, 0, 0, dpr, 0, 0);
      createField();
      draw(performance.now(), true);
    }

    function diskColor(t) {
      const green = Math.round(225 - t * 70);
      const blue = Math.round(190 - t * 115);
      return `255,${green},${blue}`;
    }

    function drawGrain(px, py, size, color, alpha) {
      context.beginPath();
      context.arc(px, py, size, 0, TAU);
      context.fillStyle = `rgba(${color},${alpha})`;
      context.fill();
    }

    function drawDiskPass(time, still, cx, cy, horizon, farPass, ramp) {
      const flat = 0.12;
      for (const grain of disk) {
        const angle = grain.phi + (still ? 0 : time * 0.002 * (0.9 / Math.pow(grain.a, 1.5)));
        const sinA = Math.sin(angle);
        if (farPass ? sinA >= 0 : sinA < 0) continue;
        const cosA = Math.cos(angle);
        const radius = grain.a * horizon;
        const beam = 1 + 0.35 * -cosA;
        const t = (grain.a - 1.35) / 1.25;
        const color = diskColor(t);
        const alpha = Math.min(1, ramp * grain.heat * (0.5 + 0.25 * beam));
        const size = grain.size * (0.85 + beam * 0.2);

        if (farPass) {
          const wrap = -sinA;
          const ringR = horizon * (1.08 + t * 0.2) + grain.drift * horizon;
          const px = cx + cosA * (radius + (ringR - radius) * wrap);
          const lift = wrap * (radius * flat + (ringR - radius * flat) * wrap);
          drawGrain(px, cy - lift, size, color, alpha);
          drawGrain(px, cy + lift * 0.92, size * 0.85, color, alpha * 0.45);
        } else {
          const px = cx + cosA * radius;
          const py = cy + sinA * radius * flat + grain.drift * horizon;
          drawGrain(px, py, size, color, alpha);
        }
      }
    }

    function draw(time, still = false) {
      if (still) {
        context.clearRect(0, 0, width, height);
      } else {
        context.globalCompositeOperation = "destination-out";
        context.fillStyle = "rgba(0,0,0,0.18)";
        context.fillRect(0, 0, width, height);
        context.globalCompositeOperation = "source-over";
      }
      const cx = width * 0.5;
      const cy = height * 0.52;
      const horizon = Math.min(width, height) * 0.19;
      const ramp = 0.6 + state * 0.12;
      const einstein = horizon * (1.0 + state * 0.04);

      for (const star of stars) {
        const sx = star.x * width;
        const sy = star.y * height;
        const dx = sx - cx;
        const dy = sy - cy;
        const r = Math.hypot(dx, dy) || 1;
        const lensed = (r + Math.sqrt(r * r + 4 * einstein * einstein)) / 2;
        if (lensed < horizon * 1.1) continue;
        const lx = cx + (dx / r) * lensed;
        const ly = cy + (dy / r) * lensed;
        if (lx < -8 || lx > width + 8 || ly < -8 || ly > height + 8) continue;
        const flicker = still ? 0.7 : 0.5 + 0.4 * Math.sin(time * 0.0011 + star.twinkle);
        context.beginPath();
        context.arc(lx, ly, star.size, 0, TAU);
        context.fillStyle = star.warm
          ? `rgba(255,215,175,${0.32 * flicker})`
          : `rgba(200,212,238,${0.34 * flicker})`;
        context.fill();
      }

      context.globalCompositeOperation = "lighter";
      drawDiskPass(time, still, cx, cy, horizon, true, ramp);

      context.globalCompositeOperation = "source-over";

      context.beginPath();
      context.arc(cx, cy, horizon, 0, TAU);
      context.fillStyle = "#04050a";
      context.fill();

      context.beginPath();
      context.arc(cx, cy, horizon * 1.03, 0, TAU);
      context.strokeStyle = `rgba(255,228,190,${0.62 + state * 0.1})`;
      context.lineWidth = 1.8;
      context.shadowBlur = 26;
      context.shadowColor = "rgba(255,180,115,0.85)";
      context.stroke();
      context.shadowBlur = 0;

      context.globalCompositeOperation = "lighter";
      drawDiskPass(time, still, cx, cy, horizon, false, ramp);
      context.globalCompositeOperation = "source-over";
    }

    function canAnimate() {
      return !reducedMotion.matches && !userPaused && visible && !document.hidden;
    }

    function loop(time) {
      raf = 0;
      if (!canAnimate()) return;
      lastTime = time;
      draw(time);
      raf = requestAnimationFrame(loop);
    }

    function startFrameLoop() {
      if (canAnimate() && !raf) raf = requestAnimationFrame(loop);
    }

    function stopFrameLoop() {
      if (raf) cancelAnimationFrame(raf);
      raf = 0;
    }

    function scheduleStep() {
      clearTimeout(timer);
      if (!canAnimate()) return;
      timer = window.setTimeout(() => {
        setState((state + 1) % 4, false);
        scheduleStep();
      }, state === 3 ? 2900 : 1900);
    }

    function updatePauseButton() {
      const motionPaused = reducedMotion.matches || userPaused;
      pauseButton.setAttribute("aria-pressed", String(motionPaused));
      pauseLabel.textContent = reducedMotion.matches ? "Motion reduced" : userPaused ? "Play motion" : "Pause motion";
      pauseButton.disabled = reducedMotion.matches;
      setGlobalMotionPaused(motionPaused);
    }

    function setState(nextState, pauseFromChoice = false) {
      state = Math.max(0, Math.min(3, Number(nextState)));
      atlas.dataset.atlasState = String(state);
      if (pauseFromChoice && !reducedMotion.matches) {
        userPaused = true;
        stopFrameLoop();
        clearTimeout(timer);
        updatePauseButton();
      }
      draw(performance.now(), !canAnimate());
    }

    pauseButton.addEventListener("click", () => {
      if (reducedMotion.matches) return;
      userPaused = !userPaused;
      updatePauseButton();
      if (userPaused) {
        stopFrameLoop();
        clearTimeout(timer);
        draw(performance.now(), true);
      } else {
        startFrameLoop();
        scheduleStep();
      }
    });

    function applyReducedMotionPreference() {
      stopFrameLoop();
      clearTimeout(timer);
      if (reducedMotion.matches) {
        setState(3);
        updatePauseButton();
        draw(performance.now(), true);
        return;
      }

      updatePauseButton();
      draw(performance.now(), true);
      // canAnimate preserves the user's independent pause choice and the page visibility state.
      startFrameLoop();
      scheduleStep();
    }

    if (typeof reducedMotion.addEventListener === "function") {
      reducedMotion.addEventListener("change", applyReducedMotionPreference);
    } else if (typeof reducedMotion.addListener === "function") {
      reducedMotion.addListener(applyReducedMotionPreference);
    }

    if ("IntersectionObserver" in window) {
      const observer = new IntersectionObserver(([entry]) => {
        visible = entry.isIntersecting;
        if (visible) {
          startFrameLoop();
          scheduleStep();
        } else {
          stopFrameLoop();
          clearTimeout(timer);
        }
      }, { threshold: 0.05 });
      observer.observe(atlas);
    }

    document.addEventListener("visibilitychange", () => {
      if (document.hidden) {
        stopFrameLoop();
        clearTimeout(timer);
      } else {
        startFrameLoop();
        scheduleStep();
      }
    });

    if ("ResizeObserver" in window) new ResizeObserver(resize).observe(canvas);
    else window.addEventListener("resize", resize, { passive: true });

    resize();
    setState(state);
    updatePauseButton();
    startFrameLoop();
    scheduleStep();
  }

  async function copyText(text) {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text);
      return;
    }
    const textarea = document.createElement("textarea");
    textarea.value = text;
    textarea.setAttribute("readonly", "");
    textarea.style.position = "fixed";
    textarea.style.opacity = "0";
    document.body.append(textarea);
    let copied = false;
    try {
      textarea.select();
      copied = document.execCommand("copy");
    } finally {
      textarea.remove();
    }
    if (!copied) throw new Error("The browser rejected the clipboard copy command.");
  }

  function setupCopyButtons() {
    const buttons = [...document.querySelectorAll("[data-copy]")];
    if (!buttons.length) return;
    document.body.classList.add("copy-ready");

    buttons.forEach((button) => {
      button.addEventListener("click", async () => {
        const source = document.getElementById(button.dataset.copy);
        const label = button.querySelector("span");
        if (!source || !label) return;
        const original = label.textContent;
        try {
          await copyText(source.textContent.trim());
          label.textContent = "Copied";
          button.setAttribute("aria-label", "Copied to clipboard");
        } catch (_) {
          label.textContent = "Select text";
          button.setAttribute("aria-label", "Copy failed; select the code manually");
        }
        window.setTimeout(() => {
          label.textContent = original;
          button.removeAttribute("aria-label");
        }, 1800);
      });
    });
  }

  function init() {
    setupMenu();
    animateHero();
    setupReveals();
    setupActiveNavigation();
    setupScrollEffects();
    setupAtlas();
    setupCopyButtons();
    document.documentElement.classList.replace("no-js", "js");
    window.__phoenixReady = true;
  }

  init();
})();
