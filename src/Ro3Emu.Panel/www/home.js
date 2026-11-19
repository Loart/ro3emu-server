// The public realm site uses the panel's existing session, permissions, news and game data.
'use strict';

function siteSection(title, content, style = '') {
  return h('section', { class: 'site-section ' + style },
    h('h2', { class: 'site-heading' }, icon('leaf'), title), content);
}

function homeShortcut(href, glyph, label, style) {
  return link(href, { class: 'home-shortcut ' + style },
    h('span', { class: 'shortcut-disc' }, icon(glyph)), h('span', null, label));
}

function realmMasthead(status, session) {
  const account = session?.account;
  const shortcuts = h('div', { class: 'home-shortcuts' },
    !account && session?.registration ? homeShortcut('/register', 'user', 'Sign up', 'red')
      : homeShortcut('/rankings', 'trophy', 'Rankings', 'red'),
    homeShortcut('/play', 'download', 'Launcher', 'green'),
    homeShortcut(account ? '/account' : '/login', 'key', 'Player panel', 'blue'));
  return [
    h('div', { class: 'home-utility' },
      h('span', { class: 'realm-online' }, 'Realm online'),
      link('/online', null, num(status.players), status.players === 1 ? ' player online' : ' players online'),
      h('button', { type: 'button', title: 'Switch theme', 'aria-label': 'Switch theme',
        onclick: () => document.getElementById('theme').click() }, icon('sun'))),
    h('header', { class: 'chronicle-mast' }, h('div', { class: 'mast-copy' },
      h('h1', null, status.name),
      status.tagline && h('p', { class: 'mast-tagline' }, status.tagline),
      shortcuts)),
    h('nav', { class: 'home-nav', 'aria-label': 'Realm navigation' },
      link('/', { 'aria-current': 'page' }, 'Home'), link('/news', null, 'News'),
      link('/events', null, 'Events'), link('/database', null, 'Database'),
      link('/rankings', null, 'Rankings'), link('/guilds', null, 'Guilds'),
      link(account ? '/account' : '/login', null, 'Player panel'))];
}

function playerRail(session, mine) {
  const account = session?.account;
  const accountContent = account
    ? h('div', { class: 'site-account' }, h('strong', null, account),
      mine?.characters.length ? h('ul', { class: 'home-characters' }, mine.characters.slice(0, 4).map(c =>
        h('li', null, link('/characters/' + c.id, null, c.name), h('small', null, 'Lv. ', c.level)))) : null,
      link('/account', { class: 'site-button' }, 'Open player panel'),
      link('/account/security', { class: 'rail-link' }, 'Account security'),
      session.admin && link('/admin', { class: 'rail-link' }, 'Staff panel'),
      h('button', { type: 'button', class: 'rail-link', onclick: logout }, 'Log out'))
    : h('div', null, playerLoginForm(), session?.registration &&
      link('/register', { class: 'rail-link' }, 'Create an account'));
  const search = h('form', { class: 'rail-search', role: 'search', action: '/items', method: 'get' },
    h('label', { for: 'home-item' }, 'Search items'),
    h('div', null, h('input', { class: 'input', type: 'search', id: 'home-item', name: 'q', placeholder: 'Name or ID' }),
      h('button', { type: 'submit', 'aria-label': 'Search items' }, icon('search'))));
  search.addEventListener('submit', event => {
    event.preventDefault();
    navigate(listHref('/items', { q: new FormData(search).get('q').trim() }));
  });
  return h('aside', { class: 'player-rail', 'aria-label': 'Player tools' },
    siteSection('Player panel', accountContent), siteSection('Database', search));
}

function chronicleNews(news) {
  return siteSection('News & Updates',
    h('div', { class: 'news-column' },
      news.length ? news.map(n => h('article', { class: 'chronicle-story' },
        h('div', { class: 'story-meta' }, n.pinned && h('span', null, 'Pinned'), day(n.published)),
        h('h3', null, link('/news', null, n.title)), n.body && h('p', { class: 'news-excerpt' }, n.body),
        n.author && h('small', null, 'Posted by ', n.author)))
      : h('article', { class: 'chronicle-story' },
        h('h3', null, 'Welcome back to Midgard'),
        h('p', null, 'Years ago, Midgard Chronicles was home to our Ragnarok Online community. Now we’re opening a new chapter together.'),
        h('p', null, 'Here for the first time? Start with the launcher guide, or look up the monsters, items, and maps that await you.'),
        link('/play', { class: 'story-link' }, 'Read the launcher guide')),
      link('/news', { class: 'archive-link' }, 'News archive')));
}

function worldColumn(status) {
  return h('aside', { class: 'world-column', 'aria-label': 'Realm information' },
    siteSection('Events & Headlines', link('/events', { class: 'event-cover' },
      h('span', null, 'Realm event calendar'), h('small', null, 'View the schedule')), 'event-section'),
    siteSection('Midgard at a Glance', facts([
      ['Players online', link('/online', null, num(status.players))], ['Characters', num(status.characters)],
      ['Accounts', num(status.accounts)], ['Uptime', duration(status.uptimeSeconds)]]), 'glance-section'));
}

async function homePage() {
  const session = state.session;
  const [status, news, level, mine] = await Promise.all([api('GET', '/api/status'),
    api('GET', '/api/news?limit=5'), api('GET', '/api/rankings/level'),
    session?.account ? api('GET', '/api/account') : null]);
  const leaders = level.rows.length ? h('ol', { class: 'home-leaders' }, level.rows.slice(0, 3).map(r =>
    h('li', null, h('span', null, r.rank), link('/profiles/' + encodeURIComponent(r.name), null, r.name), h('small', null, 'Lv. ', r.level))))
    : h('p', { class: 'leaders-empty' }, 'The first adventurers will appear here as they gain levels.');
  return h('div', { class: 'chronicle-home' }, realmMasthead(status, session),
    h('div', { class: 'home-paper' }, playerRail(session, mine),
      h('div', { class: 'paper-center' }, chronicleNews(news.news), siteSection('Realm Leaders',
        h('div', null, leaders, link('/rankings', { class: 'archive-link' }, 'View rankings')))),
      worldColumn(status)));
}

function launcherGuidePage() {
  return [pageHead({ crumbs: [{ href: '/', label: 'Home' }, { label: 'Launcher' }], title: 'Windows launcher' }),
    card('Playing on Midgard Chronicles', null, body(
      h('p', null, 'The Windows launcher is in testing. Its public download is pending the first verified character entry on the hosted realm.'),
      h('p', { class: 'guide-next' }, 'You can use the player panel now to manage your account and explore the game database.'),
      h('div', { class: 'row' }, link(state.session?.account ? '/account' : '/login', { class: 'btn primary' }, 'Player panel'),
        link('/database', { class: 'btn secondary' }, 'Game database'))))];
}
