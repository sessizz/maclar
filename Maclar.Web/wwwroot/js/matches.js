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
        apply();
        syncUrl();
    }

    searchInput?.addEventListener('input', update);
    leagueSelect?.addEventListener('change', update);
    dayButtons.forEach(btn => btn.addEventListener('click', () => {
        day = btn.dataset.day;
        update();
    }));

    // Seçili gün artık listede yoksa (ör. eski bir link) filtreyi bırak.
    if (day && !dayButtons.some(b => b.dataset.day === day)) {
        day = '';
    }

    apply();
})();
