'use strict';

const routes = ['live', 'players', 'bots', 'records', 'admin'];
const categories = { '2v10-win': '2v10 Win', '2v10-16-0': '2v10 16-0', '2vmax': '2vMax' };
const number = new Intl.NumberFormat('en');
const date = new Intl.DateTimeFormat('en', { dateStyle: 'medium', timeStyle: 'short' });
const view = document.querySelector('#view');
const status = document.querySelector('#connection');
const filters = { map: '', category: '', duo: '', side: '' };
let data = { live: {}, history: {} };
let loadError = false;
let profile;
let profileMessage = '';
let trackerUpdated = false;
let activeView;
let profileRequest;
let adminPending = false;

function el(tag, attributes = {}, ...children) {
  const node = document.createElement(tag);
  for (const [name, value] of Object.entries(attributes)) {
    if (value != null) node.setAttribute(name, value);
  }
  node.append(...children.flat().filter(child => child != null));
  return node;
}

function title(text, description) {
  return el('div', { class: 'page-head' }, el('h1', {}, text), description && el('p', {}, description));
}

function panel(heading, ...children) {
  return el('section', { class: 'panel' },
    heading && el('div', { class: 'panel-heading' }, el('h2', {}, heading)), ...children);
}

function empty(message) {
  return el('p', { class: 'empty' }, message);
}

function table(headers, rows) {
  return el('div', { class: 'scroll', tabindex: '0', 'aria-label': 'Statistics table' },
    el('table', {},
      el('thead', {}, el('tr', {}, headers.map(header => el('th', { scope: 'col' }, header)))),
      el('tbody', {}, rows.map(row => el('tr', {}, row.map(cell => el('td', {}, cell)))))));
}

function button(label, callback, className) {
  const result = el('button', { type: 'button', class: className }, label);
  result.addEventListener('click', callback);
  return result;
}

function selectField(label, id, options, value = '') {
  const select = el('select', { id }, options.map(([key, name]) => el('option', { value: key }, name)));
  if (options.some(([key]) => key === value)) select.value = value;
  return [el('label', {}, label, select), select];
}

function stats(row = {}) {
  return [number.format(row.kills ?? 0), number.format(row.deaths ?? 0),
    number.format(row.headshots ?? 0), number.format(row.headshotPercentage ?? 0) + '%'];
}

function playerCells(player) {
  return [player.name, ...stats(player), number.format(player.clutches ?? 0), number.format(player.defuses ?? 0)];
}

const playerHeaders = ['Player', 'Kills', 'Deaths', 'HS', 'HS %', '1v5+ Clutches', 'Defuses'];

function mapLabel(key) {
  return (data.live.maps ?? []).find(map => map.key === key)?.label ?? key;
}

function teamScore(side, score) {
  return el('div', { class: 'team-score ' + side.toLowerCase() },
    el('span', {}, side), el('strong', {}, score ?? '—'));
}

function livePage() {
  const live = data.live;
  const active = live.online && live.map;
  if (!active) {
    return [title('Live Match'),
      panel('Server status', empty(loadError ? 'The tracker could not load server data.'
        : trackerUpdated ? 'No live match. The game server may be starting up or offline.' : 'Connecting to the server…'))];
  }
  const roster = [...(live.roster ?? [])];
  const humans = roster.filter(player => !player.bot).length;
  let state = 'Live';
  if (live.warmup) state = 'Warmup';
  else if (live.finished) state = 'Match ended';
  else if (live.paused) state = 'Paused';
  else if (live.freezeTime) state = 'Freeze time';
  let summary;
  if (live.mode.startsWith('bot-dm')) {
    summary = el('div', { class: 'score' }, el('h2', {}, 'Deathmatch'));
  } else {
    summary = el('div', { class: 'score' }, teamScore('CT', live.teams?.CT),
      el('span', { class: 'versus' }, 'VS'), teamScore('T', live.teams?.T));
  }
  const score = el('section', { class: 'panel hero' },
    el('div', { class: 'hero-top' }, el('h2', {}, mapLabel(live.map)), el('span', { class: 'chip' }, state)),
    summary,
    el('div', { class: 'metadata' }, el('span', {}, live.mode),
      el('span', {}, humans + (humans === 1 ? ' player' : ' players') + ' · ' + (roster.length - humans) + ' bots'),
      !live.warmup && live.round > 0 && el('span', {}, 'Round ' + live.round + (live.maxRounds ? ' / ' + live.maxRounds : '')),
      live.record ? el('span', { class: 'chip' }, categories[live.record]) :
        live.challenge && el('span', { class: 'chip' }, '16-0 Challenge · ' + live.challenge)));
  roster.sort((a, b) => Number(a.bot) - Number(b.bot) || (b.stats?.kills ?? 0) - (a.stats?.kills ?? 0));
  const rows = roster.map(player => [player.name, el('span', { class: player.side.toLowerCase() }, player.side),
    player.bot ? 'Bot' : 'Player', ...stats(player.stats ?? {})]);
  return [title('Live Match'), score, panel('Scoreboard', rows.length
    ? table(['Name', 'Side', 'Type', 'Kills', 'Deaths', 'HS', 'HS %'], rows)
    : empty('The server is waiting for players.'))];
}

function leaderboardRows(group) {
  return Object.values(group ?? {}).sort((a, b) => b.kills - a.kills || a.deaths - b.deaths ||
    a.name.localeCompare(b.name, 'en'));
}

function playerPage() {
  const rows = leaderboardRows(data.history.players).map(playerCells);
  return [title('Players', 'Lifetime competitive and deathmatch statistics.'),
    panel(null, rows.length ? table(playerHeaders, rows) : empty('Player statistics will appear after the first tracked match events.'))];
}

function botPage() {
  const rows = leaderboardRows(data.history.bots).map((bot, index) => [
    el('span', {}, el('span', { class: 'rank' }, String(index + 1)), bot.name),
    bot.role ?? '—', ...stats(bot), number.format(bot.spoiledSweeps ?? 0),
    number.format(bot.plants ?? 0), number.format(bot.rescues ?? 0)]);
  return [title('Bot Hall of Fame', 'Lifetime bot statistics, ranked by kills.'),
    panel(null, rows.length
      ? table(['Bot', 'Role', 'Kills', 'Deaths', 'HS', 'HS %', '16-0s Spoiled', 'Bombs Planted', 'Hostages Rescued'], rows)
      : empty('The bot roster will appear when the game server sends tracker data.'))];
}

function duoKey(record) {
  return record.key.first + '/' + record.key.second;
}

function recordPage() {
  const records = data.history.records ?? [];
  const maps = new Map((data.live.maps ?? []).map(map => [map.key, map.label]));
  const duos = new Map();
  for (const record of records) {
    if (!maps.has(record.key.map)) maps.set(record.key.map, record.key.map);
    duos.set(duoKey(record), record.names.join(' + '));
  }
  const dropdowns = [
    ['Map', 'map', [['', 'All maps'], ...maps]],
    ['Challenge', 'category', [['', 'All challenges'], ...Object.entries(categories)]],
    ['Duo', 'duo', [['', 'All duos'], ...duos]],
    ['Side', 'side', [['', 'Both sides'], ['CT', 'CT'], ['T', 'T']]],
  ].map(([label, key, choices]) => {
    const [field, select] = selectField(label, 'filter-' + key, choices, filters[key]);
    select.addEventListener('change', () => {
      filters[key] = select.value;
      render(true);
    });
    return field;
  });
  const visible = records.filter(record =>
    (!filters.map || record.key.map === filters.map) &&
    (!filters.category || record.key.category === filters.category) &&
    (!filters.side || record.key.side === filters.side) &&
    (!filters.duo || duoKey(record) === filters.duo));
  visible.sort((a, b) => mapLabel(a.key.map).localeCompare(mapLabel(b.key.map), 'en') ||
    a.key.category.localeCompare(b.key.category, 'en') ||
    (b.best?.bots ?? 0) - (a.best?.bots ?? 0) ||
    ((b.best?.roundsWon ?? 0) - (b.best?.roundsLost ?? 0)) -
      ((a.best?.roundsWon ?? 0) - (a.best?.roundsLost ?? 0)) ||
    (a.best?.roundsLost ?? 0) - (b.best?.roundsLost ?? 0) ||
    a.names.join(' + ').localeCompare(b.names.join(' + '), 'en'));
  const cards = visible.map(record => {
    const best = record.best;
    const result = best ? best.roundsWon + '–' + best.roundsLost + ' vs ' + best.bots + ' bots' : 'No winning result';
    return el('article', { class: 'panel' }, el('div', { class: 'panel-body' },
      el('div', { class: 'record-title' }, el('h2', {}, record.names.join(' + ')), el('span', { class: 'record-score' }, result)),
      el('p', { class: 'record-line' }, [mapLabel(record.key.map), categories[record.key.category],
        record.key.side, number.format(record.attempts) + (record.attempts === 1 ? ' attempt' : ' attempts'),
        best && date.format(new Date(best.finishedAt))].filter(Boolean).join(' · '))),
    best && table(playerHeaders, Object.values(best.players).map(playerCells)));
  });
  return [title('Map Records', 'Best results and attempt counts for each duo.'),
    panel(null, el('div', { class: 'filters' }, dropdowns),
      cards.length ? el('div', { class: 'records-list' }, cards)
        : empty('No attempts match these filters.'))];
}

function getToken() {
  return sessionStorage.getItem('repserver-admin-session');
}

async function api(path, body) {
  const headers = {};
  const token = getToken();
  if (token && path !== '/api/tracker') headers.Authorization = 'Bearer ' + token;
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const response = await fetch(path, {
    headers, method: body === undefined ? 'GET' : 'POST', body: JSON.stringify(body),
  });
  const result = await response.json();
  if (!response.ok) throw new Error(result.error ?? 'Request failed.');
  return result;
}

function sessionHint() {
  return panel(null, el('div', { class: 'panel-body' },
    el('p', { class: 'hint' }, 'Type ', el('code', {}, '!trackeradmin'), ' in CS2 and open your personal admin link in this browser.'),
    profileMessage && el('p', { class: 'message error', role: 'status' }, profileMessage)));
}

function adminPage() {
  if (!profile) return [title('Admin'), sessionHint()];
  if (!profile.admin) return [title('Admin'), panel(null, empty('This Steam account does not have admin access.'))];
  const result = el('p', { class: 'message section-gap', role: 'status' });
  const run = async (command, argument, confirmText) => {
    if (adminPending || (confirmText && !window.confirm(confirmText))) return;
    adminPending = true;
    result.classList.remove('error');
    result.textContent = 'Waiting for the game server…';
    try {
      result.textContent = (await api('/api/admin', { command, argument })).message;
    } catch (error) {
      result.classList.add('error');
      result.textContent = error.message;
    } finally {
      adminPending = false;
    }
  };
  const match = panel('Match controls', el('div', { class: 'panel-body' },
    el('div', { class: 'actions' }, button('Go live', () => run('live')),
      button('Pause', () => run('pause')), button('Resume', () => run('unpause')),
      button('Restart match', () => run('restart', undefined, 'Restart the current match?'), 'danger')),
    el('div', { class: 'actions' }, button('Start T 16-0', () => run('16t', undefined, 'Set up the T 16-0 challenge?')),
      button('Start CT 16-0', () => run('16ct', undefined, 'Set up the CT 16-0 challenge?')))));
  const [mapField, map] = selectField('Map', 'admin-map',
    [['', 'Select a map'], ...(data.live.maps ?? []).map(row => [row.command, row.label])]);
  const [modeField, mode] = selectField('Mode', 'admin-mode', [
    ['bot-game', 'Competitive · bots on T'], ['bot-game-t', 'Competitive · bots on CT'],
    ['bot-dm', 'Deathmatch'], ['bot-dm-pistol', 'Pistol Deathmatch'],
    ['win', '2v10 Win'], ['16', '2v10 16-0'], ['max', '2vMax']]);
  const game = panel('Map and mode', el('div', { class: 'panel-body' },
    el('form', { class: 'form-row' }, mapField, button('Change map', () => {
      if (map.value) run('map', map.value, 'Change to ' + map.selectedOptions[0].textContent + '?');
    })),
    el('form', { class: 'form-row' }, modeField, button('Change mode', () =>
      run('mode', mode.value, 'Change the current game mode?')))));
  const count = el('input', { id: 'bot-count', type: 'number', value: '10', min: '1', max: '99', required: '' });
  const fill = side => {
    if (count.reportValidity()) run('set_bot_' + side.toLowerCase(), count.value);
  };
  const bots = panel('Bots', el('div', { class: 'panel-body' },
    el('label', {}, 'Target count', count),
    el('div', { class: 'toolbar' }, button('Fill T', () => fill('T')), button('Fill CT', () => fill('CT')),
      button('Remove all bots', () => run('kick_bot', undefined, 'Remove all bots from the server?'), 'danger'))));
  return [title('Admin', 'Manage the server while connected to CS2 as an admin.'),
    el('div', { class: 'grid' }, match, game, bots), result];
}

const pages = { live: livePage, players: playerPage, bots: botPage, records: recordPage,
  admin: adminPage };

function render(keepFocus = false) {
  const focus = keepFocus ? document.activeElement?.id : '';
  const offsets = [...view.querySelectorAll('.scroll')].map(node => node.scrollLeft);
  view.replaceChildren(...pages[activeView]());
  if (keepFocus) {
    if (focus) document.getElementById(focus)?.focus();
    view.querySelectorAll('.scroll').forEach((node, index) => { node.scrollLeft = offsets[index] ?? 0; });
  }
}

function navigate() {
  const hash = location.hash.slice(1);
  const token = /^admin\/([0-9]{17}\.[0-9a-f]{48})$/.exec(hash)?.[1];
  if (token) {
    sessionStorage.setItem('repserver-admin-session', token);
    history.replaceState(null, '', '#admin');
    profile = undefined;
    profileRequest = undefined;
  }
  const next = location.hash.slice(1);
  activeView = routes.includes(next) ? next : 'live';
  for (const link of document.querySelectorAll('nav a')) {
    if (link.hash === '#' + activeView) link.setAttribute('aria-current', 'page');
    else link.removeAttribute('aria-current');
  }
  render();
  if (activeView === 'admin' && !profile && getToken() && !profileRequest) {
    const request = api('/api/profile');
    profileRequest = request;
    request.then(result => {
      if (profileRequest !== request) return;
      profile = result;
      profileMessage = '';
    }).catch(error => {
      if (profileRequest === request) profileMessage = error.message;
    }).finally(() => {
      if (profileRequest !== request) return;
      profileRequest = undefined;
      if (activeView === 'admin') render();
    });
  }
}

async function refresh() {
  const previousMaps = JSON.stringify(data.live.maps);
  const previousHistory = JSON.stringify(data.history);
  try {
    data = await api('/api/tracker');
    loadError = false;
    trackerUpdated = true;
  } catch {
    loadError = true;
  }
  const label = loadError ? 'Tracker unavailable' : data.live.online ? 'Server online' : 'Server offline';
  status.classList.toggle('online', !loadError && Boolean(data.live.online));
  status.lastElementChild.textContent = label;
  document.querySelector('#updated').textContent = data.live.updatedAt ?
    'Last server update ' + date.format(new Date(data.live.updatedAt)) : '';
  const mapsChanged = previousMaps !== JSON.stringify(data.live.maps);
  const historyChanged = previousHistory !== JSON.stringify(data.history);
  if (activeView === 'live' || (activeView === 'records' && (mapsChanged || historyChanged)) ||
    (['players', 'bots'].includes(activeView) && historyChanged) || (activeView === 'admin' && mapsChanged && !adminPending)) {
    render(true);
  }
  setTimeout(refresh, 2500);
}

window.addEventListener('hashchange', navigate);
navigate();
refresh();
