// Save text produced by the app as a file on the user's computer.
window.sraDownload = function (fileName, contentType, text) {
    const blob = new Blob([text], { type: contentType });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 2000);
};

// Keep --nav-h equal to the sticky top bar's height (it wraps on narrow screens) so
// the risk-model side column can stick just below it.
window.sraWatchNav = function () {
    const nav = document.querySelector(".topnav");
    if (!nav || !window.ResizeObserver) return;
    new ResizeObserver(() => document.documentElement.style.setProperty("--nav-h", nav.offsetHeight + "px")).observe(nav);
};

// Hover readout for the project-finish chart (HtmlReport.SCurve with interactive: true).
// Its div.scurve carries per-day data in data-scurve: for every calendar day, how many
// iterations finished by the end of that day. A crosshair snaps to the nearest day and one
// tooltip lists every series; with the chart focused, arrow keys move by a day (Shift: a week).
// Blazor re-creates the chart markup after each run, so charts are set up lazily on first use.
(function () {
    const SVG = "http://www.w3.org/2000/svg";
    const DAY = 1440;
    const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    const charts = new WeakMap();
    let active = null;

    function svgEl(name, attrs) {
        const e = document.createElementNS(SVG, name);
        for (const k in attrs) e.setAttribute(k, attrs[k]);
        return e;
    }

    function div(cls, text) {
        const e = document.createElement("div");
        e.className = cls;
        if (text) e.textContent = text;
        return e;
    }

    // Rounded down, so the first date that reads 80% is the P80 date in the confidence table.
    function pct(c, n) {
        if (c === 0) return "0%";
        if (c === n) return "100%";
        const p = 100 * c / n;
        return p < 1 ? "<1%" : p > 99 ? ">99%" : Math.floor(p) + "%";
    }

    function setup(box) {
        const d = JSON.parse(box.dataset.scurve);
        const svg = box.querySelector("svg");
        const bars = svg.querySelectorAll("rect.bar");
        const days = d.series[0].cum.length;
        const plotW = d.w - d.l - d.r;
        const epoch = Date.parse(d.date0 + "T00:00:00Z") - d.day0 * 60000; // ms at minute 0
        const X = t => d.l + (t - d.lo) / (d.hi - d.lo) * plotW;
        const Y = p => d.t + (1 - p) * (d.h - d.t - d.b);
        const clampDay = k => Math.max(0, Math.min(days - 1, k));
        const detDay = clampDay(Math.floor((d.det - d.day0) / DAY));

        // dd-MMM-yyyy like the axis labels; dd-MMM when the year is obvious.
        function date(minutes, withYear) {
            const t = new Date(epoch + minutes * 60000);
            const s = String(t.getUTCDate()).padStart(2, "0") + "-" + MONTHS[t.getUTCMonth()];
            return withYear ? s + "-" + t.getUTCFullYear() : s;
        }

        const layer = svgEl("g", { "pointer-events": "none", visibility: "hidden" });
        const line = svgEl("line", { y1: d.t, y2: d.h - d.b, class: "scurve-x" });
        layer.appendChild(line);
        const dots = d.series.map(s => layer.appendChild(svgEl("circle", { r: 4.5, class: "scurve-dot", fill: s.color })));
        svg.appendChild(layer);

        const tip = div("scurve-tip");
        tip.hidden = true;
        tip.setAttribute("aria-live", "polite");
        box.appendChild(tip);

        let current = null, lit = null;

        function show(k) {
            k = clampDay(k);
            current = k;
            const end = d.day0 + (k + 1) * DAY;
            const x = X(Math.max(d.lo, Math.min(d.hi, end)));
            line.setAttribute("x1", x);
            line.setAttribute("x2", x);
            d.series.forEach((s, i) => {
                dots[i].setAttribute("cx", x);
                dots[i].setAttribute("cy", Y(s.cum[k] / s.n));
            });
            layer.setAttribute("visibility", "visible");

            const nb = d.bins.length;
            const bin = Math.max(0, Math.min(nb - 1, Math.floor((end - d.lo) / (d.hi - d.lo) * nb)));
            if (lit) lit.classList.remove("on");
            lit = bars[bin] || null;
            if (lit) lit.classList.add("on");

            const head = div("tip-h", "Finish by ");
            head.appendChild(document.createElement("b")).textContent = date(d.day0 + k * DAY, true);
            const rows = [head];
            for (const s of d.series) {
                const r = div("tip-r");
                r.appendChild(document.createElement("i")).style.background = s.color;
                r.appendChild(document.createElement("b")).textContent = pct(s.cum[k], s.n);
                r.appendChild(document.createElement("span")).textContent = s.name;
                rows.push(r);
            }
            if (k === Math.floor((d.det - d.day0) / DAY)) rows.push(div("tip-n", "Deterministic finish (CPM)"));
            // A Must Finish By at 00:00 is met by finishing the day before.
            if (d.mfb != null && k === Math.floor((d.mfb - 1 - d.day0) / DAY)) rows.push(div("tip-n", "Must Finish By"));
            const n0 = d.series[0].n;
            const from = d.lo + (d.hi - d.lo) * bin / nb, to = d.lo + (d.hi - d.lo) * (bin + 1) / nb;
            rows.push(div("tip-n", "Bar: " + pct(d.bins[bin], n0) + " of " + (d.series.length > 1 ? "pre-mitigation " : "") +
                "runs finish " + date(from, false) + " – " + date(to, false)));
            tip.replaceChildren(...rows);

            tip.hidden = false;
            const r = svg.getBoundingClientRect(), br = box.getBoundingClientRect();
            const sx = r.left - br.left + x * r.width / d.w;
            let left = sx + 14;
            if (left + tip.offsetWidth > br.width) left = sx - 14 - tip.offsetWidth;
            tip.style.left = Math.max(0, left) + "px";
            tip.style.top = (r.top - br.top + d.t * r.height / d.h) + "px";
            active = api;
        }

        function hide() {
            layer.setAttribute("visibility", "hidden");
            tip.hidden = true;
            if (lit) { lit.classList.remove("on"); lit = null; }
            if (active === api) active = null;
        }

        // Nearest day boundary to the pointer; the readout is "finished by the end of day k".
        function dayAt(clientX) {
            const r = svg.getBoundingClientRect();
            const t = d.lo + ((clientX - r.left) / r.width * d.w - d.l) / plotW * (d.hi - d.lo);
            return Math.round((t - d.day0) / DAY) - 1;
        }

        svg.addEventListener("pointermove", e => show(dayAt(e.clientX)));
        svg.addEventListener("pointerdown", e => show(dayAt(e.clientX)));
        svg.addEventListener("pointerleave", e => {
            if (e.pointerType === "mouse" && !svg.matches(":focus-visible")) hide();
        });
        svg.addEventListener("focus", () => { if (svg.matches(":focus-visible")) show(current ?? detDay); });
        svg.addEventListener("blur", hide);
        svg.addEventListener("keydown", e => {
            const k = current ?? detDay, step = e.shiftKey ? 7 : 1;
            if (e.key === "ArrowRight") show(k + step);
            else if (e.key === "ArrowLeft") show(k - step);
            else if (e.key === "Home") show(0);
            else if (e.key === "End") show(days - 1);
            else if (e.key === "Escape") hide();
            else return;
            e.preventDefault();
        });

        const api = { box, hide, focus: () => { if (svg.matches(":focus-visible")) show(current ?? detDay); } };
        return api;
    }

    function init(e) {
        const box = e.target instanceof Element ? e.target.closest(".scurve") : null;
        if (!box || charts.has(box)) return;
        const api = setup(box);
        charts.set(box, api);
        if (e.type === "focusin") api.focus();
    }

    document.addEventListener("pointerover", init);
    document.addEventListener("focusin", init);
    // Touch readouts stay up after the finger lifts; a tap elsewhere clears them.
    document.addEventListener("pointerdown", e => {
        if (active && !active.box.contains(e.target)) active.hide();
    });
})();
