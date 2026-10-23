(() => {
  const modes = ['light', 'dark', 'system', 'boomer'];
  const preferenceKey = 'pointbridge-theme';
  const media = window.matchMedia('(prefers-color-scheme: dark)');
  let preference = 'dark';
  let shell;
  try { preference = localStorage.getItem(preferenceKey) || 'dark'; } catch {}
  if (!modes.includes(preference)) preference = 'dark';

  function applyTheme() {
    const theme = !shell || shell.hidden ? 'dark' : preference === 'system' ? (media.matches ? 'dark' : 'light') : preference;
    document.documentElement.dataset.theme = theme;
    document.documentElement.style.colorScheme = theme === 'dark' ? 'dark' : 'light';
    document.querySelectorAll('[data-theme-choice]').forEach(button => {
      button.setAttribute('aria-pressed', String(button.dataset.themeChoice === preference));
    });
  }

  function chooseTheme(mode) {
    preference = modes.includes(mode) ? mode : 'dark';
    try { localStorage.setItem(preferenceKey, preference); } catch {}
    applyTheme();
  }

  applyTheme();
  media.addEventListener('change', applyTheme);
  window.addEventListener('storage', event => {
    if (event.key !== preferenceKey && event.key !== null) return;
    preference = modes.includes(event.newValue) ? event.newValue : 'dark';
    applyTheme();
  });
  document.addEventListener('DOMContentLoaded', () => {
    shell = document.getElementById('app-shell');
    new MutationObserver(applyTheme).observe(shell, { attributes: true, attributeFilter: ['hidden'] });
    document.querySelectorAll('.mark').forEach(mark => {
      mark.textContent = '';
      const image = document.createElement('img');
      image.src = '/brand.svg';
      image.alt = '';
      image.width = 40;
      image.height = 40;
      mark.append(image);
    });
    document.querySelectorAll('.sidebar-foot').forEach(container => {
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
    const sidebar = document.querySelector('aside');
    sidebar.id = 'workspace-menu';
    const toolbar = document.createElement('div');
    toolbar.className = 'workspace-toolbar';
    const toggle = document.createElement('button');
    toggle.type = 'button';
    toggle.className = 'secondary menu-toggle';
    toggle.setAttribute('aria-controls', sidebar.id);
    let collapsed = false;
    try { collapsed = localStorage.getItem('pointbridge-menu-collapsed') === 'true'; } catch {}
    function applyMenu() {
      sidebar.hidden = collapsed;
      document.documentElement.dataset.menuCollapsed = String(collapsed);
      toggle.setAttribute('aria-expanded', String(!collapsed));
      toggle.textContent = collapsed ? '☰ Show menu' : '☰ Hide menu';
    }
    toggle.addEventListener('click', () => {
      collapsed = !collapsed;
      try { localStorage.setItem('pointbridge-menu-collapsed', String(collapsed)); } catch {}
      applyMenu();
    });
    toolbar.append(toggle);
    document.querySelector('main').prepend(toolbar);
    applyMenu();
    applyTheme();
  });
})();
