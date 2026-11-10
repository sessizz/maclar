// Tüm sayfalarda ortak davranışlar.
(function () {
    // Açık / koyu tema. İlk değer _Layout'taki satır içi script ile sayfa çizilmeden uygulanır.
    document.getElementById('themeToggle')?.addEventListener('click', function () {
        const dark = document.documentElement.classList.toggle('dark');
        try {
            localStorage.setItem('theme', dark ? 'dark' : 'light');
        } catch (e) { /* gizli pencere vb. */ }
    });

    // "/" tuşu arama kutusuna odaklanır (bir alana yazı yazılmıyorsa).
    const search = document.getElementById('matchSearch');
    document.addEventListener('keydown', function (e) {
        if (e.key !== '/' || !search) {
            return;
        }
        const tag = (document.activeElement?.tagName || '').toLowerCase();
        if (tag === 'input' || tag === 'textarea' || tag === 'select') {
            return;
        }
        e.preventDefault();
        search.focus();
    });

    // Enter sayfayı yeniden yüklemesin; filtre zaten yazarken uygulanıyor.
    search?.addEventListener('keydown', function (e) {
        if (e.key === 'Enter') {
            e.preventDefault();
        }
    });

    // "Verileri yenile": yenilemeden sonra aynı sayfaya, aynı filtrelerle dönülsün.
    document.querySelectorAll('[data-refresh-form]').forEach(function (form) {
        form.addEventListener('submit', function () {
            form.querySelector('input[name="returnUrl"]').value = window.location.pathname + window.location.search;
            form.querySelector('input[name="search"]').value = search ? search.value.trim() : '';
            const button = form.querySelector('button[type="submit"]');
            button.disabled = true;
            button.classList.add('is-loading');
        });
    });
})();
