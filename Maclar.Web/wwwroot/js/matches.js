// Maç ve salon listelerinde arama + gün + küme filtresi. Tamamen tarayıcıda çalışır;
// sunucu her zaman tüm listeyi gönderir. Durum adres çubuğunda tutulur (?search=, ?gun=, ?kume=),
// böylece link paylaşılabilir ve sayfa yenilenince aynı filtre geri gelir.
(function () {
    const root = document.querySelector('[data-filter-root]');
    if (!root) {
        return;
    }

    const searchInput = document.getElementById('matchSearch');
    const leagueSelect = root.querySelector('[data-league-filter]');
    const leagueMenu = root.querySelector('[data-league-menu]');
    const favoriteContainer = root.querySelector('[data-favorite-leagues]');
    const leagueChoices = Array.from(root.querySelectorAll('[data-league-choice]'));
    const leagueStars = Array.from(root.querySelectorAll('[data-league-star]'));
    const favoriteKey = 'favoriteLeagues';
    let favorites = [];
    try {
        const saved = JSON.parse(localStorage.getItem(favoriteKey) || '[]');
        if (Array.isArray(saved)) favorites = [...new Set(saved.filter(v => typeof v === 'string' && v))];
    } catch { /* Depolama kapalıysa favoriler bu sayfada kullanılabilir. */ }

    function renderLeagueControls() {
        if (!leagueSelect || !leagueMenu) return;
        const league = leagueSelect.value;
        root.querySelector('[data-league-label]').textContent = league || 'Tüm kümeler';
        leagueChoices.forEach(btn => btn.setAttribute('aria-pressed', String(btn.dataset.leagueChoice === league)));
        leagueStars.forEach(btn => {
            const on = favorites.includes(btn.dataset.leagueStar);
            const action = on ? 'favorilerden çıkar' : 'favorilere ekle';
            btn.setAttribute('aria-pressed', String(on));
            btn.setAttribute('aria-label', `${btn.dataset.leagueStar} ${action}`);
            btn.title = on ? 'Favorilerden çıkar' : 'Favorilere ekle';
        });
        favoriteContainer.replaceChildren();
        favorites.filter(value => Array.from(leagueSelect.options).some(o => o.value === value)).forEach(value => {
            const btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'badge badge-outline favorite-league';
            btn.textContent = `★ ${value}`;
            btn.setAttribute('aria-label', `${value} kümesini filtrele`);
            btn.setAttribute('aria-pressed', String(league === value));
            btn.addEventListener('click', () => {
                leagueSelect.value = leagueSelect.value === value ? '' : value;
                update();
            });
            favoriteContainer.append(btn);
        });
        favoriteContainer.hidden = favoriteContainer.childElementCount === 0;
    }

    const dayButtons = Array.from(root.querySelectorAll('[data-day]')).filter(el => el.tagName === 'BUTTON');
    const rows = Array.from(root.querySelectorAll('tr[data-row]'));
    const groups = Array.from(root.querySelectorAll('tr[data-group]'));
    const noResults = root.querySelector('[data-no-results]');
    const counter = root.querySelector('[data-result-count]');
    const noun = counter?.dataset.noun || 'maç';

    const params = new URLSearchParams(window.location.search);
    let day = params.get('gun') || '';

    if (leagueSelect && params.get('kume')) {
        leagueSelect.value = params.get('kume');
    }

    const normalize = text => (text || '').toLocaleLowerCase('tr-TR');

    // Boşlukla ayrılmış kelimelerin hepsi eşleşmeli; "tırnaklı ifade" tek kelime sayılır.
    function parseTokens(raw) {
        const tokens = [];
        const regex = /"([^"]+)"|(\S+)/g;
        let m;
        while ((m = regex.exec(raw)) !== null) {
            const value = m[1] || m[2];
            if (value) {
                tokens.push(normalize(value));
            }
        }
        return tokens;
    }

    function apply() {
        const tokens = parseTokens(searchInput ? searchInput.value : '');
        const league = leagueSelect ? leagueSelect.value : '';
        let visible = 0;

        rows.forEach(row => {
            const haystack = row.dataset.search || '';
            const show = tokens.every(t => haystack.includes(t))
                && (!day || row.dataset.day === day)
                && (!league || row.dataset.league === league);
            row.hidden = !show;
            if (show) {
                visible++;
            }
        });

        // Gün başlığını, altındaki satırların hepsi gizliyse gizle.
        // Başlıktaki sayı, o günün filtreye uyan maç sayısını göstersin.
        groups.forEach(group => {
            const key = group.dataset.group;
            const count = rows.filter(r => r.dataset.day === key && !r.hidden).length;
            group.hidden = count === 0;
            const label = group.querySelector('[data-group-count]');
            if (label) {
                label.textContent = `${count} maç`;
            }
        });

        dayButtons.forEach(btn => {
            const on = btn.dataset.day === day;
            btn.classList.toggle('is-on', on);
            btn.setAttribute('aria-pressed', on ? 'true' : 'false');
        });

        if (noResults) {
            noResults.hidden = visible > 0;
        }
        if (counter) {
            counter.textContent = `Gösterilen: ${visible} / ${rows.length} ${noun}`;
        }
    }

    function syncUrl() {
        const url = new URL(window.location.href);
        const set = (key, value) => value ? url.searchParams.set(key, value) : url.searchParams.delete(key);
        set('search', searchInput ? searchInput.value.trim() : '');
        set('gun', day);
        set('kume', leagueSelect ? leagueSelect.value : '');
        window.history.replaceState(null, '', url);
    }

    function update() {
        renderLeagueControls();
        apply();
        syncUrl();
    }

    searchInput?.addEventListener('input', update);
    leagueSelect?.addEventListener('change', update);
    leagueChoices.forEach(btn => btn.addEventListener('click', () => {
        leagueSelect.value = btn.dataset.leagueChoice;
        leagueMenu.open = false;
        leagueMenu.querySelector('summary').focus();
        update();
    }));
    leagueStars.forEach(btn => btn.addEventListener('click', () => {
        const value = btn.dataset.leagueStar;
        favorites = favorites.includes(value) ? favorites.filter(v => v !== value) : [...favorites, value];
        try { localStorage.setItem(favoriteKey, JSON.stringify(favorites)); } catch { /* Sayfa içindeki seçim korunur. */ }
        renderLeagueControls();
    }));
    document.addEventListener('click', event => {
        if (leagueMenu && !leagueMenu.contains(event.target)) leagueMenu.open = false;
    });
    leagueMenu?.addEventListener('keydown', event => {
        if (event.key === 'Escape') {
            leagueMenu.open = false;
            leagueMenu.querySelector('summary').focus();
        }
    });
    dayButtons.forEach(btn => btn.addEventListener('click', () => {
        day = btn.dataset.day;
        update();
    }));

    // Seçili gün artık listede yoksa (ör. eski bir link) filtreyi bırak.
    if (day && !dayButtons.some(b => b.dataset.day === day)) {
        day = '';
    }

    renderLeagueControls();
    apply();
})();
