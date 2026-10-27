// Banka Eşleştirme (admin): "Elle eşle" kutusunda müşteri araması.
//
// CSP (deploy/Caddyfile): script-src 'self' — bu dosya wwwroot'tan gelir, satır içi script ya da olay işleyicisi yok.
// Arama POST'la gider (antiforgery belirteci satırın formundan): sorgu, yani ad parçası, URL'ye ve erişim günlüğüne
// girmez. Sunucu lisansı hareketten türetir (Index.cshtml.cs, OnPostCustomerSearchAsync).
//
// Seçim, müşterinin kimliğini gizli CustomerId alanına yazar; kutuda yazı değişince kimlik silinir. Kimlik boşken form
// eski yolla kutudaki tam kullanıcı adını gönderir (JS'siz de çalışır).
// Klavye: Aşağı/Yukarı listede gezer, Enter seçer (liste açıkken formu göndermez), Esc listeyi kapatır.
(function () {
  'use strict';

  var MIN_LENGTH = 2;
  var DELAY_MS = 250;

  var table = document.querySelector('table[data-search-url]');
  if (!table) return;
  var searchUrl = table.getAttribute('data-search-url');
  var inputs = table.querySelectorAll('input[data-customer-search]');
  for (var i = 0; i < inputs.length; i++) setup(inputs[i]);

  function labelOf(hit) {
    return [hit.username, hit.name, hit.platform].filter(function (p) { return p; }).join(' · ');
  }

  function setup(input) {
    var form = input.form;
    var hidden = form.querySelector('input[data-customer-id]');
    var list = document.getElementById(input.getAttribute('aria-controls'));
    var token = form.querySelector('input[name="__RequestVerificationToken"]');
    var tx = form.querySelector('input[name="TransactionId"]');
    if (!hidden || !list || !token || !tx) return;

    var timer = 0;
    var seq = 0;          // yalnız son isteğin yanıtı gösterilir
    var controller = null;
    var hits = [];
    var active = -1;

    input.addEventListener('input', function () {
      hidden.value = '';
      clearTimeout(timer);
      var q = input.value.trim();
      if (q.length < MIN_LENGTH) { close(); return; }
      timer = setTimeout(function () { search(q); }, DELAY_MS);
    });

    input.addEventListener('keydown', function (e) {
      var open = !list.hidden;
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        if (open && hits.length) { e.preventDefault(); move(e.key === 'ArrowDown' ? 1 : -1); }
      } else if (e.key === 'Enter') {
        if (!open) return; // kapalıyken Enter formu gönderir (Elle eşle)
        e.preventDefault();
        if (active >= 0) choose(hits[active]);
        else if (hits.length === 1) choose(hits[0]);
        else if (hits.length > 1) move(1);
      } else if (e.key === 'Escape') {
        if (open) { e.preventDefault(); close(); }
      }
    });

    input.addEventListener('blur', function () { setTimeout(close, 150); });
    // Tıklama sırasında odak kutuda kalsın (blur listeyi kapatmasın).
    list.addEventListener('mousedown', function (e) { e.preventDefault(); });
    list.addEventListener('click', function (e) {
      var li = e.target.closest('li[data-index]');
      if (li) choose(hits[Number(li.getAttribute('data-index'))]);
    });

    function search(q) {
      var my = ++seq;
      if (controller) controller.abort();
      controller = typeof AbortController === 'function' ? new AbortController() : null;
      var body = new URLSearchParams();
      body.set('__RequestVerificationToken', token.value);
      body.set('TransactionId', tx.value);
      body.set('q', q);
      fetch(searchUrl, {
        method: 'POST',
        body: body,
        credentials: 'same-origin',
        headers: { 'Accept': 'application/json' },
        signal: controller ? controller.signal : undefined
      }).then(function (r) {
        // Oturum düşmüşse giriş sayfasına yönlenir: HTML gelir, JSON değil.
        if (!r.ok || r.redirected) throw new Error('arama');
        return r.json();
      }).then(function (result) {
        if (my === seq) render(result);
      }).catch(function (err) {
        if (my === seq && !(err && err.name === 'AbortError')) {
          message('Arama yapılamadı; oturum düşmüş olabilir, sayfayı yenileyin.');
        }
      });
    }

    function render(result) {
      hits = Array.isArray(result) ? result : [];
      active = -1;
      list.textContent = '';
      input.removeAttribute('aria-activedescendant');
      if (!hits.length) { message('Eşleşen müşteri yok.'); return; }
      hits.forEach(function (hit, index) {
        var li = document.createElement('li');
        li.id = list.id + '-' + index;
        li.className = 'list-group-item list-group-item-action py-1 small';
        li.setAttribute('role', 'option');
        li.setAttribute('aria-selected', 'false');
        li.setAttribute('data-index', String(index));
        li.textContent = labelOf(hit); // textContent: ad/kullanıcı adı HTML olarak yorumlanmaz
        list.appendChild(li);
      });
      show();
    }

    function message(text) {
      hits = [];
      active = -1;
      list.textContent = '';
      var li = document.createElement('li');
      li.className = 'list-group-item py-1 small text-muted';
      li.setAttribute('role', 'option');
      li.setAttribute('aria-disabled', 'true');
      li.textContent = text;
      list.appendChild(li);
      show();
    }

    function show() {
      list.hidden = false;
      input.setAttribute('aria-expanded', 'true');
    }

    function close() {
      seq++; // yoldaki yanıt listeyi yeniden açmasın
      clearTimeout(timer);
      if (controller) { controller.abort(); controller = null; }
      hits = [];
      active = -1;
      list.hidden = true;
      list.textContent = '';
      input.setAttribute('aria-expanded', 'false');
      input.removeAttribute('aria-activedescendant');
    }

    function move(delta) {
      var n = hits.length;
      active = active < 0 ? (delta > 0 ? 0 : n - 1) : (active + delta + n) % n;
      var items = list.querySelectorAll('li[data-index]');
      for (var k = 0; k < items.length; k++) {
        var on = k === active;
        items[k].classList.toggle('active', on);
        items[k].setAttribute('aria-selected', on ? 'true' : 'false');
        if (on) {
          input.setAttribute('aria-activedescendant', items[k].id);
          items[k].scrollIntoView({ block: 'nearest' });
        }
      }
    }

    function choose(hit) {
      if (!hit) return;
      // Kutuya seçimin etiketi yazılır (aynı ad iki platformdaysa hangisi seçildi görünsün); sunucu kimliği okur.
      // Önce kutu, sonra kimlik: kutunun input olayı kimliği silmez (programla atama olay üretmez).
      input.value = labelOf(hit);
      hidden.value = hit.id;
      close();
    }
  }
})();
