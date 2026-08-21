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
    const stepButtons = [...document.querySelectorAll("[data-atlas-step]")];
    if (!atlas || !scene || !canvas || !context || !pauseButton || !pauseLabel) return;

    let state = reducedMotion.matches ? 3 : 0;
    let userPaused = reducedMotion.matches;
    let visible = true;
    let width = 1;
    let height = 1;
    let dpr = 1;
    let nodes = [];
    let raf = 0;
    let timer = 0;
    let lastTime = performance.now();

    function createNodes() {
      const random = mulberry32(20260710);
      const count = width < 520 ? 38 : 58;
      nodes = Array.from({ length: count }, (_, index) => {
        const angle = random() * Math.PI * 2;
        const radius = 0.18 + random() * 0.29;
        return {
          angle,
          radius,
          speed: (0.018 + random() * 0.026) * (random() > 0.5 ? 1 : -1),
          size: 0.6 + random() * 1.25,
          group: index % 4,
          phase: random() * Math.PI * 2
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
      createNodes();
      draw(performance.now(), true);
    }

    function nodePosition(node, time, still) {
      const motion = still ? 0 : time * node.speed * 0.00012;
      const angle = node.angle + motion;
      const squeeze = 0.82 + Math.sin(node.phase) * 0.07;
      return {
        x: width * 0.5 + Math.cos(angle) * width * node.radius,
        y: height * 0.5 + Math.sin(angle) * height * node.radius * squeeze
      };
    }

    function drawPath(points, progress, color) {
      if (progress <= 0) return;
      context.save();
      context.beginPath();
      context.moveTo(points[0].x * width, points[0].y * height);
      const last = Math.min(points.length - 1, Math.ceil(progress));
      for (let index = 1; index <= last; index += 1) {
        const point = points[index];
        context.lineTo(point.x * width, point.y * height);
      }
      context.strokeStyle = color;
      context.lineWidth = 1.3;
      context.shadowBlur = 10;
      context.shadowColor = color;
      context.setLineDash([4, 7]);
      context.stroke();
      context.restore();
    }

    function draw(time, still = false) {
      context.clearRect(0, 0, width, height);
      const positions = nodes.map((node) => nodePosition(node, time, still));

      for (let index = 0; index < nodes.length; index += 1) {
        const node = nodes[index];
        const point = positions[index];
        const active = state > 0 && node.group <= Math.min(3, state);
        const color = node.group === 0 ? "91,214,255" : node.group === 1 ? "141,114,255" : node.group === 2 ? "255,107,61" : "201,255,121";

        if (active) {
          for (let next = index + 1; next < nodes.length; next += 1) {
            if (nodes[next].group !== node.group) continue;
            const other = positions[next];
            const distance = Math.hypot(point.x - other.x, point.y - other.y);
            if (distance > width * 0.16) continue;
            context.beginPath();
            context.moveTo(point.x, point.y);
            context.lineTo(other.x, other.y);
            context.strokeStyle = `rgba(${color},${(1 - distance / (width * 0.16)) * 0.13})`;
            context.lineWidth = 0.55;
            context.stroke();
          }
        }

        context.beginPath();
        context.arc(point.x, point.y, node.size + (active ? 0.55 : 0), 0, Math.PI * 2);
        context.fillStyle = `rgba(${color},${active ? 0.78 : 0.27})`;
        if (active) {
          context.shadowBlur = 9;
          context.shadowColor = `rgba(${color},.72)`;
        }
        context.fill();
        context.shadowBlur = 0;
      }

      const route = [
        { x: 0.2, y: 0.46 },
        { x: 0.5, y: 0.5 },
        { x: 0.79, y: 0.34 },
        { x: 0.76, y: 0.79 }
      ];
      drawPath(route, state, "rgba(91,214,255,.82)");

      if (state > 0) {
        const segment = Math.min(state, route.length - 1);
        const from = route[segment - 1];
        const to = route[segment];
        const phase = still ? 1 : (time % 1600) / 1600;
        const x = (from.x + (to.x - from.x) * phase) * width;
        const y = (from.y + (to.y - from.y) * phase) * height;
        context.beginPath();
        context.arc(x, y, 3.2, 0, Math.PI * 2);
        context.fillStyle = "#f8f7f3";
        context.shadowBlur = 15;
        context.shadowColor = "#5bd6ff";
        context.fill();
        context.shadowBlur = 0;
      }
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
      pauseButton.setAttribute("aria-pressed", String(userPaused));
      pauseLabel.textContent = reducedMotion.matches ? "Motion reduced" : userPaused ? "Play motion" : "Pause motion";
      pauseButton.disabled = reducedMotion.matches;
      setGlobalMotionPaused(userPaused && !reducedMotion.matches);
    }

    function setState(nextState, pauseFromChoice = false) {
      state = Math.max(0, Math.min(3, Number(nextState)));
      atlas.dataset.atlasState = String(state);
      stepButtons.forEach((button) => button.setAttribute("aria-pressed", String(Number(button.dataset.atlasStep) === state)));
      if (pauseFromChoice && !reducedMotion.matches) {
        userPaused = true;
        stopFrameLoop();
        clearTimeout(timer);
        updatePauseButton();
      }
      draw(performance.now(), !canAnimate());
    }

    stepButtons.forEach((button) => {
      button.addEventListener("click", () => setState(button.dataset.atlasStep, true));
    });

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

    if (finePointer.matches && !reducedMotion.matches) {
      let pointerFrame = 0;
      let pointer = { x: 0.5, y: 0.5 };
      const renderPointer = () => {
        pointerFrame = 0;
        const rotateY = (pointer.x - 0.5) * 5;
        const rotateX = (0.5 - pointer.y) * 5;
        scene.style.transform = `perspective(950px) rotateX(${rotateX.toFixed(2)}deg) rotateY(${rotateY.toFixed(2)}deg)`;
        scene.style.setProperty("--pointer-x", `${(pointer.x * 100).toFixed(1)}%`);
        scene.style.setProperty("--pointer-y", `${(pointer.y * 100).toFixed(1)}%`);
      };
      scene.addEventListener("pointermove", (event) => {
        const rect = scene.getBoundingClientRect();
        pointer = {
          x: Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width)),
          y: Math.max(0, Math.min(1, (event.clientY - rect.top) / rect.height))
        };
        if (!pointerFrame) pointerFrame = requestAnimationFrame(renderPointer);
      });
      scene.addEventListener("pointerleave", () => {
        pointer = { x: 0.5, y: 0.5 };
        if (!pointerFrame) pointerFrame = requestAnimationFrame(renderPointer);
      });
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

  function setupTabs() {
    document.querySelectorAll("[data-tabs]").forEach((tabsRoot) => {
      const tabs = [...tabsRoot.querySelectorAll('[role="tab"]')];
      const panels = tabs.map((tab) => document.getElementById(tab.getAttribute("aria-controls")));

      function selectTab(nextTab, moveFocus = false) {
        tabs.forEach((tab, index) => {
          const selected = tab === nextTab;
          tab.setAttribute("aria-selected", String(selected));
          tab.tabIndex = selected ? 0 : -1;
          if (panels[index]) panels[index].hidden = !selected;
        });
        if (moveFocus) nextTab.focus();
      }

      tabs.forEach((tab, index) => {
        tab.addEventListener("click", () => selectTab(tab));
        tab.addEventListener("keydown", (event) => {
          let nextIndex = index;
          if (event.key === "ArrowRight") nextIndex = (index + 1) % tabs.length;
          else if (event.key === "ArrowLeft") nextIndex = (index - 1 + tabs.length) % tabs.length;
          else if (event.key === "Home") nextIndex = 0;
          else if (event.key === "End") nextIndex = tabs.length - 1;
          else return;
          event.preventDefault();
          selectTab(tabs[nextIndex], true);
        });
      });

      selectTab(tabs.find((tab) => tab.getAttribute("aria-selected") === "true") || tabs[0]);
    });
    document.body.classList.add("tabs-ready");
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
    textarea.select();
    document.execCommand("copy");
    textarea.remove();
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
    setupTabs();
    setupCopyButtons();
    document.documentElement.classList.replace("no-js", "js");
    window.__phoenixReady = true;
  }

  init();
})();
