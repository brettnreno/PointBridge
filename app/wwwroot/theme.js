(() => {
  const modes = ['light', 'dark', 'system', 'boomer'];
  const preferenceKey = 'pointbridge-theme';
  const media = window.matchMedia('(prefers-color-scheme: dark)');
  let preference = 'system';
  try { preference = localStorage.getItem(preferenceKey) || 'system'; } catch {}
  if (!modes.includes(preference)) preference = 'system';

  function applyTheme() {
    const theme = preference === 'system' ? (media.matches ? 'dark' : 'light') : preference;
    document.documentElement.dataset.theme = theme;
    document.documentElement.style.colorScheme = theme === 'dark' ? 'dark' : 'light';
    document.querySelectorAll('[data-theme-choice]').forEach(button => {
      button.setAttribute('aria-pressed', String(button.dataset.themeChoice === preference));
    });
  }

  function chooseTheme(mode) {
    preference = modes.includes(mode) ? mode : 'system';
    try { localStorage.setItem(preferenceKey, preference); } catch {}
    applyTheme();
  }

  applyTheme();
  media.addEventListener('change', applyTheme);
  window.addEventListener('storage', event => {
    if (event.key !== preferenceKey && event.key !== null) return;
    preference = modes.includes(event.newValue) ? event.newValue : 'system';
    applyTheme();
  });
  document.addEventListener('DOMContentLoaded', () => {
    document.querySelectorAll('.mark').forEach(mark => {
      mark.textContent = '';
      const image = document.createElement('img');
      image.src = '/brand.svg';
      image.alt = '';
      image.width = 40;
      image.height = 40;
      mark.append(image);
    });
    document.querySelectorAll('.sidebar-foot, .auth-card').forEach(container => {
      const selector = document.createElement('fieldset');
      selector.className = 'theme-selector';
      const legend = document.createElement('legend');
      legend.textContent = 'Appearance';
      selector.append(legend);
      modes.forEach(mode => {
        const button = document.createElement('button');
        button.type = 'button';
        button.dataset.themeChoice = mode;
        button.textContent = mode[0].toUpperCase() + mode.slice(1);
        button.addEventListener('click', () => chooseTheme(mode));
        selector.append(button);
      });
      container.append(selector);
    });
    applyTheme();
  });
})();
