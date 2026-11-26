(() => {
  if (!CSS.supports('color', 'light-dark(black, white)')) return;

  const key = 'secondmonitor-theme';
  const scheme = document.querySelector('meta[name="color-scheme"]');
  const chromeColor = document.querySelector('meta[name="theme-color"]');
  const systemDark = matchMedia('(prefers-color-scheme: dark)');
  const motion = matchMedia('(prefers-reduced-motion: no-preference)');
  let control;
  let preference;

  function applyTheme(value) {
    preference = value === 'dark' || value === 'light' ? value : null;
    scheme.content = preference || 'light dark';
    const dark = preference ? preference === 'dark' : systemDark.matches;
    chromeColor.content = dark ? '#121e19' : '#f5f6ef';

    if (control) {
      control.setAttribute('aria-pressed', String(dark));
      control.title = dark ? 'Switch to light theme' : 'Switch to dark theme';
    }
  }

  function toggleTheme() {
    const dark = preference ? preference === 'dark' : systemDark.matches;
    const next = dark === systemDark.matches ? (dark ? 'light' : 'dark') : null;
    applyTheme(next);
    try {
      if (next) localStorage.setItem(key, next);
      else localStorage.removeItem(key);
    } catch {
      // Keep the current page usable if browser storage is blocked.
    }
  }

  try {
    applyTheme(localStorage.getItem(key));
  } catch {
    applyTheme(null);
  }

  document.addEventListener('DOMContentLoaded', () => {
    control = document.querySelector('.theme-toggle');
    applyTheme(preference);
    control.hidden = false;

    control.addEventListener('click', () => {
      if (motion.matches && document.startViewTransition) {
        document.startViewTransition(toggleTheme);
      } else {
        toggleTheme();
      }
    });
  });

  systemDark.addEventListener('change', () => applyTheme(preference));
  window.addEventListener('storage', (event) => {
    if (event.key === key || event.key === null) applyTheme(event.newValue);
  });
})();
