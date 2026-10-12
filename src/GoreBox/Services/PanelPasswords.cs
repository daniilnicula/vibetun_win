using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GoreBox.Utils;

namespace GoreBox.Services;

/// <summary>
/// Пароли для страниц вкладки «Панели». Свой менеджер вместо встроенного WebView2: тот не показывает
/// запрос «Сохранить пароль» для новых логинов в части версий рантайма (например 152), и пароли
/// молча не сохраняются. Здесь: JavaScript на странице ловит отправку формы и показывает
/// «Сохранить пароль?», а пароли хранятся в файле, зашифрованные DPAPI (только для текущего пользователя Windows).
/// </summary>
public static class PanelPasswords
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GoreBox.PanelPasswords.v1");
    private static readonly object Sync = new();

    public static string FilePath => Path.Combine(AppPaths.DataRoot, "panel-passwords.json");

    private sealed class Entry
    {
        public string Host { get; set; } = "";
        public string User { get; set; } = "";
        public string Secret { get; set; } = "";   // пароль, зашифрованный DPAPI (base64)
    }

    /// <summary>Хост страницы без www. — по нему ищем сохранённые входы («example.com» и «www.example.com» — один сайт).</summary>
    public static string HostOf(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return "";
        var host = uri.Host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    /// <summary>Последний сохранённый вход для сайта или null.</summary>
    public static (string User, string Password)? Find(string host)
    {
        lock (Sync)
        {
            foreach (var entry in Load().AsEnumerable().Reverse())
            {
                if (!string.Equals(entry.Host, host, StringComparison.Ordinal)) continue;
                var password = Unprotect(entry.Secret);
                if (password is not null) return (entry.User, password);
            }
        }
        return null;
    }

    /// <summary>Уже сохранён ли точно такой же вход (логин и пароль) — тогда повторно не предлагаем.</summary>
    public static bool Has(string host, string user, string password)
    {
        lock (Sync)
        {
            return Load().Any(e => e.Host == host && e.User == user && Unprotect(e.Secret) == password);
        }
    }

    /// <summary>Сохранить или обновить вход (пара «сайт + логин» уникальна).</summary>
    public static void Save(string host, string user, string password)
    {
        lock (Sync)
        {
            var list = Load();
            list.RemoveAll(e => e.Host == host && e.User == user);
            list.Add(new Entry { Host = host, User = user, Secret = Protect(password) });
            Write(list);
        }
    }

    /// <summary>Удалить все сохранённые пароли (вызывается вместе с очисткой входов «Панелей»).</summary>
    public static void DeleteAll()
    {
        lock (Sync)
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { /* файл занят — останется */ }
        }
    }

    private static List<Entry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<Entry>();
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath)) ?? new List<Entry>();
        }
        catch
        {
            return new List<Entry>();
        }
    }

    private static void Write(List<Entry> list)
    {
        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
        }
    }

    private static string Protect(string password)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string? Unprotect(string secret)
    {
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(secret), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;   // запись зашифрована для другого пользователя или повреждена
        }
    }

    /// <summary>
    /// Скрипт страницы (только главный документ). Сообщения страница шлёт в приложение:
    /// <c>want</c> — есть ли поле пароля и нет ли что предложить; <c>captured</c> — отправлен логин;
    /// <c>save</c> / <c>dismiss</c> — ответ на «Сохранить пароль?». От приложения получает
    /// <c>fill</c> (заполнить поля) и <c>offer</c> (показать запрос сохранения).
    /// </summary>
    public const string Script = """
    (function () {
      'use strict';
      if (window.top !== window) return;
      if (window.__gbPasswords) return;
      window.__gbPasswords = true;

      var post = function (msg) {
        try { window.chrome.webview.postMessage(JSON.stringify(msg)); } catch (e) { }
      };

      var passwordFields = function () {
        return Array.prototype.slice.call(document.querySelectorAll('input[type="password"]'))
          .filter(function (el) { return !el.disabled; });
      };

      // поле логина — последнее текстовое поле перед полем пароля (в той же форме, если она есть)
      var usernameFor = function (pw) {
        var scope = pw.form || document;
        var best = null;
        Array.prototype.slice.call(scope.querySelectorAll('input')).forEach(function (el) {
          var t = (el.type || 'text').toLowerCase();
          if (el.disabled || el === pw) return;
          if (['text', 'email', 'tel'].indexOf(t) < 0) return;
          if (el.compareDocumentPosition(pw) & Node.DOCUMENT_POSITION_FOLLOWING) best = el;
        });
        return best;
      };

      // значение через нативный сеттер: поддерживает React/Vue-формы, которые следят за событиями input
      var setValue = function (el, value) {
        var setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
        setter.call(el, value);
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
      };

      var capture = function (pw) {
        if (!pw || !pw.value) return;
        var u = usernameFor(pw);
        post({ type: 'captured', user: u ? u.value : '', pass: pw.value });
      };

      // обычная отправка формы
      document.addEventListener('submit', function (e) {
        var pw = e.target && e.target.querySelector ? e.target.querySelector('input[type="password"]') : null;
        capture(pw);
      }, true);

      // Enter в поле пароля (в том числе в формах без submit)
      document.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' && e.target && e.target.type === 'password') capture(e.target);
      }, true);

      // кнопка входа вне формы (одностраничные приложения)
      document.addEventListener('click', function (e) {
        var b = e.target && e.target.closest ? e.target.closest('button, input[type="submit"]') : null;
        if (!b || b.type === 'button' || b.type === 'reset' || b.form) return;
        capture(passwordFields().filter(function (el) { return el.value; })[0]);
      }, true);

      var fill = function (user, pass) {
        var pw = passwordFields()[0];
        if (!pw || pw.value) return;          // уже заполнено — не трогаем
        if (pw.autocomplete === 'new-password') return;   // форма регистрации — свой пароль, не подставляем
        var u = usernameFor(pw);
        if (u && !u.value && user) setValue(u, user);
        setValue(pw, pass);
      };

      var offerSave = function (user) {
        if (document.getElementById('gb-pw-offer')) return;
        var box = document.createElement('div');
        box.id = 'gb-pw-offer';
        box.setAttribute('style', 'all:initial;position:fixed;top:12px;right:12px;z-index:2147483647;' +
          'font:13px/1.4 "Segoe UI",sans-serif;color:#eef0f3;background:#1f2226;border:1px solid #3a4049;' +
          'border-radius:10px;padding:12px 14px;box-shadow:0 8px 24px rgba(0,0,0,.35);max-width:320px;');

        var text = document.createElement('div');
        text.setAttribute('style', 'all:initial;font:13px/1.4 "Segoe UI",sans-serif;color:#eef0f3;display:block;');
        text.textContent = 'GoreBox: сохранить пароль' + (user ? ' для «' + user + '»' : '') + '?';

        var row = document.createElement('div');
        row.setAttribute('style', 'all:initial;display:flex;gap:8px;justify-content:flex-end;margin-top:10px;');

        var button = function (label, primary, msg) {
          var b = document.createElement('button');
          b.textContent = label;
          b.setAttribute('style', 'all:initial;font:13px "Segoe UI",sans-serif;cursor:pointer;padding:6px 12px;' +
            'border-radius:7px;' + (primary
              ? 'background:#3bc08a;color:#0d1f17;font-weight:600;'
              : 'background:transparent;color:#aab4c0;'));
          b.addEventListener('click', function () { post({ type: msg }); box.remove(); });
          return b;
        };
        row.appendChild(button('Не сейчас', false, 'dismiss'));
        row.appendChild(button('Сохранить', true, 'save'));

        box.appendChild(text);
        box.appendChild(row);
        (document.body || document.documentElement).appendChild(box);
        setTimeout(function () { if (box.parentNode) box.parentNode.removeChild(box); }, 60000);
      };

      window.chrome.webview.addEventListener('message', function (ev) {
        var m = ev.data;
        if (typeof m === 'string') { try { m = JSON.parse(m); } catch (e) { return; } }
        if (!m) return;
        if (m.type === 'fill') fill(m.user, m.pass);
        else if (m.type === 'offer') offerSave(m.user);
      });

      var lastWant = 0, wantTimer = null;
      var want = function () {
        lastWant = Date.now();
        post({ type: 'want', hasPassword: passwordFields().length > 0 });
      };
      var scheduleWant = function () {
        clearTimeout(wantTimer);
        wantTimer = setTimeout(want, 300);
      };

      // форма может появиться позже (SPA): следим за DOM, но не чаще раза в 1.5 с
      var observer = new MutationObserver(function () {
        if (passwordFields().length > 0 && Date.now() - lastWant > 1500) scheduleWant();
      });

      var start = function () {
        observer.observe(document.documentElement, { childList: true, subtree: true });
        scheduleWant();
      };
      if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
      else start();
    })();
    """;
}
