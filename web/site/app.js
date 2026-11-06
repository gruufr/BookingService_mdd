// Ночлег — фронтенд. Чистый JS, без сборки. Все запросы идут в /api/*, nginx раздаёт их по сервисам.
(() => {
  "use strict";

  const app = document.getElementById("app");
  const state = { hotels: [], cities: [], city: "", sort: "rating", minStars: 0, dates: defaultDates(), guests: 2, email: load("email") || "" };
  let heroStop = null;

  // ---------- утилиты ----------
  function load(k) { try { return localStorage.getItem("nochleg:" + k); } catch { return null; } }
  function save(k, v) { try { localStorage.setItem("nochleg:" + k, v); } catch { /* приватный режим */ } }
  function esc(s) { return String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c])); }
  function iso(d) { const z = new Date(d.getTime() - d.getTimezoneOffset() * 6e4); return z.toISOString().slice(0, 10); }
  function addDays(s, n) { const d = new Date(s + "T00:00:00"); d.setDate(d.getDate() + n); return iso(d); }
  function defaultDates() { const t = iso(new Date()); return { in: addDays(t, 1), out: addDays(t, 3) }; }
  function nights(a, b) { return Math.round((new Date(b) - new Date(a)) / 864e5); }
  const rub = n => new Intl.NumberFormat("ru-RU").format(Math.round(n)) + " ₽";
  const fmtDate = s => new Date(s + "T00:00:00").toLocaleDateString("ru-RU", { day: "numeric", month: "short" });
  const pad = n => String(n).padStart(2, "0");
  const stars = n => "★".repeat(n) + `<span style="opacity:.25">${"★".repeat(5 - n)}</span>`;
  function plural(n, a, b, c) { const m10 = n % 10, m100 = n % 100; return m10 === 1 && m100 !== 11 ? a : m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20) ? b : c; }

  async function api(path, opts = {}) {
    const res = await fetch("/api" + path, { headers: { "Content-Type": "application/json" }, ...opts });
    let body = null;
    try { body = await res.json(); } catch { /* пустой ответ */ }
    if (!res.ok) throw new Error((body && body.error) || `Ошибка ${res.status}`);
    return body;
  }

  let toastT;
  function toast(msg, bad) {
    const t = document.getElementById("toast");
    t.textContent = msg; t.classList.toggle("bad", !!bad); t.classList.add("show");
    clearTimeout(toastT); toastT = setTimeout(() => t.classList.remove("show"), 3200);
  }

  function reveal() {
    const els = app.querySelectorAll(".rv:not(.in)");
    if (!("IntersectionObserver" in window)) { els.forEach(e => e.classList.add("in")); return; }
    const io = new IntersectionObserver(es => es.forEach(e => { if (e.isIntersecting) { e.target.classList.add("in"); io.unobserve(e.target); } }), { rootMargin: "0px 0px -8% 0px" });
    els.forEach(e => io.observe(e));
  }

  // ---------- статус сервисов ----------
  const SERVICES = [
    { id: "web", name: "web", port: 8000, role: "Сайт и API-шлюз (nginx)" },
    { id: "booking", name: "booking-service", port: 8080, role: "Брони, проверка дат, связывает сервисы" },
    { id: "hotel", name: "hotel-service", port: 8083, role: "Отели, номера, отзывы" },
    { id: "user", name: "user-service", port: 8081, role: "Гости — создаются по e-mail" },
    { id: "notification", name: "notification-service", port: 8082, role: "Журнал уведомлений" },
    { id: "db", name: "postgres:16", port: 5432, role: "4 базы, volume db_data" },
  ];
  const health = { web: true };

  async function checkHealth() {
    await Promise.all(["booking", "hotel", "user", "notification"].map(async id => {
      try { const r = await fetch(`/api/health/${id}`, { cache: "no-store" }); health[id] = r.ok; } catch { health[id] = false; }
    }));
    // база жива, если сервисы смогли из неё прочитать
    try { const r = await fetch("/api/hotels/cities", { cache: "no-store" }); health.db = r.ok; } catch { health.db = false; }
    document.querySelectorAll("[data-health]").forEach(el => { el.className = "dot " + (health[el.dataset.health] ? "ok" : "err"); });
  }

  // ---------- главная ----------
  async function viewHome(scrollTo) {
    app.innerHTML = `
      <section class="hero" id="top">
        <canvas id="field" aria-hidden="true"></canvas>
        <div class="eyebrow mono"><b>00/</b><span>Бронирование отелей</span></div>
        <h1 class="h1">Место, где хорошо спится.</h1>
        <p class="lead">Двенадцать отелей в разных уголках России — от стеклянных домиков под сиянием до шале у подъёмника. Выберите даты, остальное мы проверим.</p>
        <form class="search" id="search" autocomplete="off">
          <div class="field"><label class="mono" for="sCity">Город</label><select class="input" id="sCity"><option value="">Все города</option></select></div>
          <div class="field"><label class="mono" for="sIn">Заезд</label><input class="input" type="date" id="sIn" required></div>
          <div class="field"><label class="mono" for="sOut">Выезд</label><input class="input" type="date" id="sOut" required></div>
          <div class="field"><label class="mono" for="sGuests">Гости</label><select class="input" id="sGuests">${[1, 2, 3, 4].map(n => `<option value="${n}">${n}</option>`).join("")}</select></div>
          <button class="btn" type="submit">Найти <span class="arr">→</span></button>
          <button class="btn btn-ghost" type="button" id="sReset">Сбросить</button>
        </form>
        <div class="hero-meta mono"><span><b id="mHotels">—</b> отелей</span><span><b id="mCities">—</b> городов</span><span><b>5</b> сервисов + PostgreSQL</span></div>
      </section>

      <section class="section" id="catalog">
        <div class="sec-head">
          <div>
            <div class="eyebrow mono rv"><b>01/</b><span>Каталог</span></div>
            <h2 class="h2 rv">Выберите, где проснуться.</h2>
          </div>
          <div class="filters rv" id="filters"></div>
        </div>
        <div class="grid" id="grid">${'<div class="skeleton" style="aspect-ratio:4/3"></div>'.repeat(3)}</div>
      </section>

      <section class="section" id="system">
        <div class="sys">
          <div>
            <div class="eyebrow mono rv"><b>02/</b><span>Как это устроено</span></div>
            <h2 class="h2 rv">Пять сервисов. Одна бронь.</h2>
            <p class="lead rv">Каждый сервис — отдельный контейнер со своим API и своей базой. Точки справа — живой статус, сайт опрашивает их каждые 10 секунд.</p>
            <ol class="steps rv">
              <li><span>Сайт отправляет <code>POST /api/bookings</code> в booking-service.</span></li>
              <li><span>booking-service спрашивает у hotel-service цену и вместимость номера.</span></li>
              <li><span>Проверяет в своей базе, что даты не пересекаются с другими бронями.</span></li>
              <li><span>user-service находит гостя по e-mail или создаёт нового.</span></li>
              <li><span>Бронь сохраняется, notification-service записывает уведомление.</span></li>
            </ol>
          </div>
          <div class="flow rv">
            ${SERVICES.map((s, i) => `
              <div class="flow-row ${i >= 2 && i <= 4 ? "indent" : ""}">
                <span class="dot" data-health="${s.id}"></span>
                <div><div class="name">${s.name}</div><div class="role">${s.role}</div></div>
                <span class="port mono">:${s.port}</span>
              </div>`).join("")}
          </div>
        </div>
      </section>`;

    const f = document.getElementById("search");
    f.sIn.value = state.dates.in; f.sOut.value = state.dates.out; f.sGuests.value = state.guests;
    f.sIn.min = iso(new Date());
    f.sIn.addEventListener("change", () => { if (f.sOut.value <= f.sIn.value) f.sOut.value = addDays(f.sIn.value, 1); f.sOut.min = addDays(f.sIn.value, 1); });
    f.addEventListener("submit", e => {
      e.preventDefault();
      if (f.sOut.value <= f.sIn.value) { toast("Дата выезда должна быть позже заезда", true); return; }
      state.dates = { in: f.sIn.value, out: f.sOut.value }; state.guests = +f.sGuests.value; state.city = f.sCity.value;
      renderFilters(); renderGrid();
      document.getElementById("catalog").scrollIntoView({ behavior: "smooth" });
    });
    // Сброс всех фильтров: поиск и каталог возвращаются к значениям по умолчанию.
    document.getElementById("sReset").addEventListener("click", () => {
      state.dates = defaultDates(); state.guests = 2; state.city = ""; state.minStars = 0; state.sort = "rating";
      f.sCity.value = ""; f.sIn.value = state.dates.in; f.sOut.value = state.dates.out; f.sGuests.value = "2";
      f.sOut.min = addDays(f.sIn.value, 1);
      renderFilters(); renderGrid();
      toast("Фильтры сброшены");
    });

    heroStop = startField(document.getElementById("field"));
    reveal(); checkHealth();

    try {
      if (!state.hotels.length) {
        [state.hotels, state.cities] = await Promise.all([api("/hotels"), api("/hotels/cities")]);
      }
    } catch (e) {
      document.getElementById("grid").innerHTML = `<p class="empty">Не удалось загрузить отели: ${esc(e.message)}. Проверьте, что запущен hotel-service.</p>`;
      return;
    }
    f.sCity.insertAdjacentHTML("beforeend", state.cities.map(c => `<option>${esc(c)}</option>`).join(""));
    f.sCity.value = state.city;
    document.getElementById("mHotels").textContent = state.hotels.length;
    document.getElementById("mCities").textContent = state.cities.length;
    renderFilters(); renderGrid();
    if (scrollTo) document.getElementById(scrollTo)?.scrollIntoView();
  }

  function renderFilters() {
    const box = document.getElementById("filters");
    if (!box) return;
    box.innerHTML = `
      ${[0, 4, 5].map(s => `<button class="chip ${state.minStars === s ? "on" : ""}" data-stars="${s}">${s ? s + "★ и выше" : "Любые"}</button>`).join("")}
      <span class="sep"></span>
      <select class="input" id="fSort" aria-label="Сортировка">
        <option value="rating">По рейтингу</option><option value="price">Сначала дешевле</option><option value="-price">Сначала дороже</option><option value="stars">По звёздам</option>
      </select>`;
    box.querySelector("#fSort").value = state.sort;
    box.querySelectorAll("[data-stars]").forEach(b => b.onclick = () => { state.minStars = +b.dataset.stars; renderFilters(); renderGrid(); });
    box.querySelector("#fSort").onchange = e => { state.sort = e.target.value; renderGrid(); };
  }

  function renderGrid() {
    const grid = document.getElementById("grid");
    if (!grid) return;
    let list = state.hotels.filter(h => (!state.city || h.city === state.city) && h.stars >= state.minStars);
    const by = { rating: (a, b) => b.rating - a.rating, price: (a, b) => a.pricePerNight - b.pricePerNight, "-price": (a, b) => b.pricePerNight - a.pricePerNight, stars: (a, b) => b.stars - a.stars || b.rating - a.rating };
    list = list.sort(by[state.sort]);
    const n = nights(state.dates.in, state.dates.out);
    grid.innerHTML = list.length ? list.map((h, i) => `
      <a class="card rv" href="#/hotel/${h.id}" style="transition-delay:${(i % 3) * 60}ms">
        <div class="ph">
          <img src="${esc(h.imageUrl)}" alt="${esc(h.name)}" loading="lazy" width="1200" height="800">
          <span class="num mono">${pad(i + 1)}</span>
          <span class="score">${h.rating.toFixed(1)}</span>
        </div>
        <div class="meta mono"><span>${esc(h.city)}</span><span class="stars">${stars(h.stars)}</span></div>
        <h3>${esc(h.name)}</h3>
        <p>${esc(h.tagline)}</p>
        <div class="price">от <b>${rub(h.pricePerNight)}</b> <span class="muted">/ ночь · ${rub(h.pricePerNight * n)} за ${n} ${plural(n, "ночь", "ночи", "ночей")}</span></div>
      </a>`).join("") : `<p class="empty">Под эти фильтры ничего не нашлось. Попробуйте другой город или меньше звёзд.</p>`;
    reveal();
  }

  // Поле из точек на первом экране: медленно дышит, расступается вокруг курсора.
  function startField(cv) {
    const ctx = cv.getContext("2d");
    const reduce = matchMedia("(prefers-reduced-motion: reduce)").matches;
    const GAP = 22;
    let w, h, dpr, pts = [], raf, t = 0;
    const mouse = { x: -9999, y: -9999, tx: -9999, ty: -9999 };
    function resize() {
      dpr = Math.min(devicePixelRatio || 1, 2);
      w = cv.clientWidth; h = cv.clientHeight;
      cv.width = w * dpr; cv.height = h * dpr; ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      pts = [];
      for (let y = GAP / 2; y < h; y += GAP) for (let x = GAP / 2; x < w; x += GAP) pts.push({ x, y, s: Math.random() });
    }
    function frame() {
      t += 0.006;
      mouse.x += (mouse.tx - mouse.x) * 0.12; mouse.y += (mouse.ty - mouse.y) * 0.12;
      ctx.clearRect(0, 0, w, h);
      for (const p of pts) {
        const wave = Math.sin(p.x * 0.006 + t * 2) * Math.cos(p.y * 0.008 - t * 1.4);
        let a = 0.13 + Math.max(0, wave) * 0.45 + p.s * 0.07;
        let x = p.x, y = p.y;
        const dx = x - mouse.x, dy = y - mouse.y, d = Math.hypot(dx, dy), R = 160;
        let hot = 0;
        if (d < R) { const k = (1 - d / R) ** 2; x += (dx / (d || 1)) * k * 30; y += (dy / (d || 1)) * k * 30; hot = k; a += k * 0.8; }
        ctx.fillStyle = hot > 0.08 ? `rgba(214,195,160,${Math.min(a, 1)})` : `rgba(236,236,234,${a})`;
        const r = 1 + hot * 1.2;
        ctx.fillRect(x - r / 2, y - r / 2, r, r);
      }
      if (!reduce) raf = requestAnimationFrame(frame);
    }
    const onMove = e => { const r = cv.getBoundingClientRect(); mouse.tx = e.clientX - r.left; mouse.ty = e.clientY - r.top; };
    const onLeave = () => { mouse.tx = mouse.ty = -9999; };
    resize(); frame();
    addEventListener("resize", resize);
    cv.parentElement.addEventListener("pointermove", onMove);
    cv.parentElement.addEventListener("pointerleave", onLeave);
    return () => { cancelAnimationFrame(raf); removeEventListener("resize", resize); };
  }

  // ---------- страница отеля ----------
  async function viewHotel(id) {
    app.innerHTML = `<div class="hotel-top"><a class="back mono" href="#/">← Все отели</a></div><div class="cover skeleton"></div>`;
    let data;
    try { data = await api(`/hotels/${id}`); }
    catch (e) { app.innerHTML = `<div class="mine"><h2 class="h2">Отель не найден.</h2><p class="lead">${esc(e.message)}</p><p><a class="btn btn-ghost" href="#/">← К каталогу</a></p></div>`; return; }
    const { hotel: h, rooms, reviews } = data;
    document.title = `${h.name} — Ночлег`;
    const sel = { room: null };

    app.innerHTML = `
      <div class="hotel-top"><a class="back mono" href="#/">← Все отели</a></div>
      <div class="cover"><img src="${esc(h.imageUrl)}" alt="${esc(h.name)}"></div>
      <div class="hotel">
        <div>
          <div class="eyebrow mono"><span>${esc(h.city)}</span><span>·</span><span>${esc(h.address)}</span><span class="stars">${stars(h.stars)}</span></div>
          <h1 class="h2">${esc(h.name)}.</h1>
          <p class="desc">${esc(h.description)}</p>
          <div class="tags">${h.amenities.map(a => `<span class="tag">${esc(a)}</span>`).join("")}</div>

          <div class="block">
            <div class="mono">Отзывы гостей</div>
            <div class="rating-big"><b>${h.rating.toFixed(1)}</b><span class="muted">${h.rating >= 9 ? "Превосходно" : h.rating >= 8.5 ? "Отлично" : "Очень хорошо"} · ${h.reviewsCount} ${plural(h.reviewsCount, "отзыв", "отзыва", "отзывов")}</span></div>
            <div style="margin-top:24px">
              ${reviews.map(r => `
                <div class="review">
                  <span class="who">${esc(r.author)}</span><span class="sc">${r.score.toFixed(1)}</span>
                  <p>${esc(r.text)}</p>
                  <span class="when mono">${fmtDate(r.date)}</span>
                </div>`).join("")}
            </div>
          </div>
        </div>

        <aside class="panel" id="panel">
          <form id="bk" autocomplete="on" novalidate>
            <div style="display:flex;flex-direction:column;gap:14px">
              <div class="row2">
                <div class="field"><label class="mono" for="bIn">Заезд</label><input class="input" type="date" id="bIn" required></div>
                <div class="field"><label class="mono" for="bOut">Выезд</label><input class="input" type="date" id="bOut" required></div>
              </div>
              <div class="field"><label class="mono" for="bGuests">Гости</label><select class="input" id="bGuests">${[1, 2, 3, 4].map(n => `<option value="${n}">${n} ${plural(n, "гость", "гостя", "гостей")}</option>`).join("")}</select></div>
              <div class="field"><span class="mono" style="color:var(--dim)">Номер</span><div class="rooms" id="rooms"></div></div>
              <div class="field"><label class="mono" for="bName">Имя</label><input class="input" id="bName" name="name" autocomplete="name" placeholder="Как к вам обращаться" required></div>
              <div class="field"><label class="mono" for="bEmail">E-mail</label><input class="input" id="bEmail" name="email" type="email" autocomplete="email" placeholder="для подтверждения" required></div>
              <div class="total"><span class="muted" id="tNights"></span><b id="tSum">—</b></div>
              <p class="err-text" id="bErr"></p>
              <button class="btn" type="submit" id="bBtn">Забронировать <span class="arr">→</span></button>
              <span class="mono" style="color:var(--dim);text-align:center">Оплата при заселении · отмена бесплатно</span>
            </div>
          </form>
        </aside>
      </div>`;

    const f = document.getElementById("bk");
    f.bIn.value = state.dates.in; f.bOut.value = state.dates.out; f.bGuests.value = state.guests;
    f.bIn.min = iso(new Date()); f.bOut.min = addDays(f.bIn.value, 1);
    f.bEmail.value = state.email;
    const roomImg = i => `/img/room-${Math.min(i + 1, 3)}.jpg`;

    async function refreshRooms() {
      if (f.bOut.value <= f.bIn.value) f.bOut.value = addDays(f.bIn.value, 1);
      f.bOut.min = addDays(f.bIn.value, 1);
      state.dates = { in: f.bIn.value, out: f.bOut.value }; state.guests = +f.bGuests.value;
      let busy = [];
      try { busy = (await api(`/availability?hotelId=${h.id}&checkIn=${f.bIn.value}&checkOut=${f.bOut.value}`)).busyRoomIds; }
      catch { /* booking-service недоступен — покажем все */ }
      const box = document.getElementById("rooms");
      box.innerHTML = rooms.map((r, i) => {
        const off = busy.includes(r.id) || r.capacity < state.guests;
        return `<button type="button" class="room ${sel.room === r.id && !off ? "on" : ""}" data-id="${r.id}" ${off ? "disabled" : ""} title="${r.capacity < state.guests ? "Мало мест для выбранного числа гостей" : ""}">
          <img src="${roomImg(i)}" alt=""><span><span class="t">${esc(r.type)}</span><br><span class="c">до ${r.capacity} ${plural(r.capacity, "гостя", "гостей", "гостей")}</span></span><span class="p">${rub(r.price)}</span></button>`;
      }).join("");
      if (!rooms.some(r => r.id === sel.room && !busy.includes(r.id) && r.capacity >= state.guests)) {
        const first = rooms.find(r => !busy.includes(r.id) && r.capacity >= state.guests);
        sel.room = first ? first.id : null;
        box.querySelector(`[data-id="${sel.room}"]`)?.classList.add("on");
      }
      box.querySelectorAll(".room:not([disabled])").forEach(b => b.onclick = () => { sel.room = +b.dataset.id; box.querySelectorAll(".room").forEach(x => x.classList.toggle("on", x === b)); total(); });
      total();
    }
    function total() {
      const n = nights(f.bIn.value, f.bOut.value), r = rooms.find(x => x.id === sel.room);
      document.getElementById("tNights").textContent = `${n} ${plural(n, "ночь", "ночи", "ночей")}`;
      document.getElementById("tSum").textContent = r ? rub(r.price * n) : "нет мест";
      document.getElementById("bBtn").disabled = !r;
    }
    f.bIn.onchange = f.bOut.onchange = f.bGuests.onchange = refreshRooms;
    await refreshRooms();

    f.addEventListener("submit", async e => {
      e.preventDefault();
      const err = document.getElementById("bErr"); err.textContent = "";
      const name = f.bName.value.trim(), email = f.bEmail.value.trim();
      if (name.length < 2) { err.textContent = "Укажите имя"; f.bName.focus(); return; }
      if (!/^\S+@\S+\.\S+$/.test(email)) { err.textContent = "Проверьте e-mail"; f.bEmail.focus(); return; }
      const btn = document.getElementById("bBtn"); btn.disabled = true; btn.firstChild.textContent = "Бронируем… ";
      try {
        const b = await api("/bookings", { method: "POST", body: JSON.stringify({ roomId: sel.room, checkIn: f.bIn.value, checkOut: f.bOut.value, guests: +f.bGuests.value, name, email }) });
        state.email = b.email; save("email", b.email);
        document.getElementById("panel").innerHTML = `
          <div class="done">
            <div class="mono accent">Бронь подтверждена</div>
            <div class="big">№ ${pad(b.id)}</div>
            <p class="muted" style="margin:0 0 12px">Уведомление ${b.notified ? "записано в журнал" : "не доставлено — сервис уведомлений недоступен"} для ${esc(b.email)}.</p>
            <div class="line"><span>Отель</span><span>${esc(b.hotelName)}</span></div>
            <div class="line"><span>Номер</span><span>${esc(b.roomType)}</span></div>
            <div class="line"><span>Даты</span><span>${fmtDate(b.checkIn)} — ${fmtDate(b.checkOut)}</span></div>
            <div class="line"><span>Гости</span><span>${b.guests}</span></div>
            <div class="line"><span>Итого</span><span>${rub(b.totalPrice)}</span></div>
            <a class="btn" href="#/bookings" style="width:100%;margin-top:16px">Мои брони <span class="arr">→</span></a>
          </div>`;
        toast("Готово! Бронь №" + b.id);
      } catch (ex) {
        err.textContent = ex.message; btn.disabled = false; btn.firstChild.textContent = "Забронировать ";
        refreshRooms();
      }
    });
  }

  // ---------- мои брони ----------
  async function viewBookings() {
    app.innerHTML = `
      <section class="mine">
        <div class="eyebrow mono"><b>03/</b><span>Мои брони</span></div>
        <h1 class="h2">Ваши поездки.</h1>
        <p class="lead">Регистрация не нужна: брони привязаны к e-mail, который вы указали при бронировании.</p>
        <form class="lookup" id="lk"><input class="input" type="email" id="lkEmail" placeholder="you@example.com" required autocomplete="email"><button class="btn" type="submit">Показать <span class="arr">→</span></button></form>
        <div id="list"></div>
      </section>`;
    const f = document.getElementById("lk");
    f.lkEmail.value = state.email;
    f.addEventListener("submit", e => { e.preventDefault(); state.email = f.lkEmail.value.trim().toLowerCase(); save("email", state.email); renderMine(); });
    if (state.email) renderMine();
  }

  async function renderMine() {
    const box = document.getElementById("list");
    box.innerHTML = `<div class="skeleton" style="height:120px"></div>`;
    try {
      const q = encodeURIComponent(state.email);
      const [list, notes] = await Promise.all([api(`/bookings?email=${q}`), api(`/notifications?email=${q}`).catch(() => [])]);
      if (!state.hotels.length) state.hotels = await api("/hotels").catch(() => []);
      const img = id => state.hotels.find(h => h.id === id)?.imageUrl || "/img/room-1.jpg";
      box.innerHTML = (list.length ? list.map(b => `
        <div class="bk ${b.status === "Cancelled" ? "cancelled" : ""}">
          <a href="#/hotel/${b.hotelId}"><img src="${esc(img(b.hotelId))}" alt=""></a>
          <div>
            <div class="mono" style="color:var(--dim)">№ ${pad(b.id)} · ${esc(b.city)}</div>
            <h4>${esc(b.hotelName)} — ${esc(b.roomType)}</h4>
            <div class="muted">${fmtDate(b.checkIn)} — ${fmtDate(b.checkOut)} · ${b.nights} ${plural(b.nights, "ночь", "ночи", "ночей")} · ${b.guests} ${plural(b.guests, "гость", "гостя", "гостей")}</div>
          </div>
          <div class="right">
            <span class="mono st">${b.status === "Cancelled" ? "Отменена" : "Подтверждена"}</span>
            <b>${rub(b.totalPrice)}</b>
            ${b.status === "Cancelled" ? "" : `<button class="chip" data-cancel="${b.id}">Отменить</button>`}
          </div>
        </div>`).join("") : `<p class="empty">Для ${esc(state.email)} броней пока нет. <a class="accent" href="#/">Выбрать отель →</a></p>`)
        + (notes.length ? `<div class="block"><div class="mono">Уведомления · notification-service</div>${notes.map(n => `
          <div class="note"><span class="mono">${new Date(n.createdAt).toLocaleString("ru-RU", { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" })}</span><span>${esc(n.message)}</span></div>`).join("")}</div>` : "");
      box.querySelectorAll("[data-cancel]").forEach(btn => btn.onclick = async () => {
        if (!confirm("Отменить бронь №" + btn.dataset.cancel + "?")) return;
        try { await api(`/bookings/${btn.dataset.cancel}/cancel`, { method: "POST" }); toast("Бронь отменена"); renderMine(); }
        catch (e) { toast(e.message, true); }
      });
    } catch (e) {
      box.innerHTML = `<p class="empty">Не удалось загрузить брони: ${esc(e.message)}</p>`;
    }
  }

  // ---------- роутер ----------
  function route() {
    if (heroStop) { heroStop(); heroStop = null; }
    document.title = "Ночлег — бронирование отелей";
    const hash = location.hash.replace(/^#/, "") || "/";
    const m = hash.match(/^\/hotel\/(\d+)/);
    let nav = "home";
    if (m) viewHotel(+m[1]);
    else if (hash.startsWith("/bookings")) { nav = "bookings"; viewBookings(); }
    else if (hash.startsWith("/system")) { nav = "system"; viewHome("system"); }
    else viewHome();
    document.querySelectorAll("[data-nav]").forEach(a => a.classList.toggle("on", a.dataset.nav === nav));
    if (!hash.startsWith("/system")) scrollTo({ top: 0, behavior: "instant" });
  }

  addEventListener("hashchange", route);
  route();
  checkHealth();
  setInterval(checkHealth, 10000);
})();
