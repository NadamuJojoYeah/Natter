/*
 * Natter 会话控制台 · P1 只读回放
 *
 * 零构建 vanilla JS：同源、无外部依赖、无 inline script。
 * 数据全部来自宿主 IConversationQuery 端口（经插件 /api/sessions*），本页不做任何写操作。
 * 所有用户/模型内容一律用 textContent 渲染，绝不拼接 HTML 字符串。
 */
(function () {
  'use strict';

  var state = {
    token: null,
    filters: { source: '', actor: '', query: '', since: '', until: '' },
    cursor: null,
    sessions: [],
    selected: null,
    messages: [],
    after: 0,
    limits: { maxPageSize: 200, maxMessagePageSize: 200, maxMessageCharacters: 8000 },
    queryAvailable: null,
    auditRoot: null,
    busy: false
  };

  var dom = {};

  function byId(id) {
    return document.getElementById(id);
  }

  function cacheDom() {
    [
      'token-gate', 'token-form', 'token-input', 'token-submit', 'token-forget', 'token-message',
      'console', 'readonly-badge', 'query-badge',
      'filters', 'filter-source', 'filter-actor', 'filter-query', 'filter-since', 'filter-until',
      'filter-apply', 'filter-reset',
      'list-count', 'list-refresh', 'list-state', 'list-state-title', 'list-state-body',
      'session-list', 'list-more',
      'detail-empty', 'detail', 'detail-source', 'detail-meta', 'message-more',
      'detail-state', 'detail-state-title', 'detail-state-body',
      'message-list', 'detail-foot-count', 'detail-foot-loaded'
    ].forEach(function (id) {
      dom[id] = byId(id);
    });
  }

  // ------------------------------------------------------------------ helpers

  function formatTime(value) {
    if (!value) {
      return '—';
    }

    var date = new Date(value);
    if (isNaN(date.getTime())) {
      return String(value);
    }

    return date.toLocaleString('zh-CN', { hour12: false });
  }

  function element(tag, className, text) {
    var node = document.createElement(tag);
    if (className) {
      node.className = className;
    }
    if (text !== undefined && text !== null) {
      node.textContent = String(text);
    }
    return node;
  }

  function showState(prefix, kind, title, body) {
    var box = dom[prefix + '-state'];
    if (!kind) {
      box.hidden = true;
      return;
    }

    box.hidden = false;
    box.className = 'state' + (kind === 'error' ? ' state-error' : kind === 'warn' ? ' state-warn' : '');
    dom[prefix + '-state-title'].textContent = title;
    dom[prefix + '-state-body'].textContent = body || '';
  }

  function buildQuery(params) {
    var pairs = [];
    Object.keys(params).forEach(function (key) {
      var value = params[key];
      if (value === null || value === undefined || value === '') {
        return;
      }
      pairs.push(encodeURIComponent(key) + '=' + encodeURIComponent(value));
    });
    return pairs.length ? '?' + pairs.join('&') : '';
  }

  function api(path, params) {
    var headers = {};
    if (state.token) {
      headers.Authorization = 'Bearer ' + state.token;
    }

    return fetch(path + buildQuery(params || {}), {
      method: 'GET',
      headers: headers,
      cache: 'no-store',
      credentials: 'omit'
    }).then(function (response) {
      return response.text().then(function (raw) {
        var payload = null;
        if (raw) {
          try {
            payload = JSON.parse(raw);
          } catch (error) {
            payload = null;
          }
        }

        if (!response.ok) {
          var failure = new Error(
            (payload && payload.error && payload.error.message) || ('HTTP ' + response.status)
          );
          failure.status = response.status;
          failure.code = payload && payload.error ? payload.error.code : null;
          throw failure;
        }

        return payload;
      });
    });
  }

  function describeFailure(error) {
    if (error.status === 401) {
      return { kind: 'error', title: '认证失败', body: 'console token 缺失或不正确（' + (error.code || 'auth') + '）。' };
    }
    if (error.status === 403) {
      return { kind: 'error', title: '请求被拒绝', body: '来源校验未通过：请直接用回环地址（127.0.0.1 / localhost）打开本页。' };
    }
    if (error.status === 501) {
      return {
        kind: 'warn',
        title: '宿主未启用会话查询',
        body: '插件没有拿到 IConversationQuery：请在 Natter 插件配置中给 natter.plugin.conversation-console 授予 chat.read 后重启宿主。（' + (error.code || '') + '）'
      };
    }
    if (error.status === 503) {
      return { kind: 'warn', title: '控制台繁忙', body: '并发请求超出上限，请稍后重试。' };
    }
    if (error.status === 408) {
      return { kind: 'warn', title: '请求超时', body: '宿主响应超过控制台预算，请缩小范围后重试。' };
    }
    return { kind: 'error', title: '请求失败', body: (error.code ? error.code + ' · ' : '') + error.message };
  }

  // ------------------------------------------------------------------ token

  function applyToken(token) {
    state.token = token;
    dom['token-gate'].hidden = true;
    dom['console'].hidden = false;
    dom['token-forget'].hidden = false;
    dom['token-message'].hidden = true;
    loadHealth();
    loadSessions(true);
  }

  function clearToken(message) {
    state.token = null;
    dom['console'].hidden = true;
    dom['token-gate'].hidden = false;
    dom['token-forget'].hidden = true;
    if (message) {
      dom['token-message'].hidden = false;
      dom['token-message'].textContent = message;
    }
  }

  function loadHealth() {
    api('/api/health').then(function (payload) {
      state.queryAvailable = payload.query && payload.query.available === true;
      state.auditRoot = payload.audit ? payload.audit.root : null;
      if (payload.limits) {
        state.limits = payload.limits;
      }

      dom['readonly-badge'].textContent = payload.readOnly ? 'readOnly' : 'readOnly=false (!)';
      dom['readonly-badge'].className = payload.readOnly ? 'badge badge-ro' : 'badge badge-warn';
      dom['query-badge'].textContent = 'chat.read: ' + (state.queryAvailable ? 'granted' : 'missing');
      dom['query-badge'].className = state.queryAvailable ? 'badge badge-ro' : 'badge badge-warn';
    }).catch(function (error) {
      dom['query-badge'].textContent = 'chat.read: unknown';
      dom['query-badge'].className = 'badge badge-warn';
      if (error.status === 401) {
        clearToken('token 无效：' + (error.code || 'auth.invalid'));
      }
    });
  }

  // ------------------------------------------------------------------ session list

  function sessionParams(reset) {
    return {
      source: state.filters.source,
      actor: state.filters.actor,
      query: state.filters.query,
      since: state.filters.since,
      until: state.filters.until,
      cursor: reset ? '' : (state.cursor || '')
    };
  }

  function loadSessions(reset) {
    if (state.busy) {
      return;
    }

    state.busy = true;
    dom['filter-apply'].disabled = true;

    if (reset) {
      state.sessions = [];
      state.cursor = null;
      renderSessions();
      dom['list-count'].textContent = '加载中…';
      showState('list', null);
    }

    api('/api/sessions', sessionParams(reset)).then(function (payload) {
      if (reset) {
        state.sessions = payload.items || [];
      } else {
        state.sessions = state.sessions.concat(payload.items || []);
      }

      state.cursor = payload.nextCursor || null;
      renderSessions();
      dom['list-count'].textContent = state.sessions.length + ' 个会话' + (state.cursor ? '（还有更多）' : '');
      dom['list-more'].hidden = !state.cursor;

      if (state.sessions.length === 0) {
        showState('list', 'empty', '没有匹配的会话', '调整 source / actor / 时间范围或搜索词后重试。');
      } else {
        showState('list', null);
      }
    }).catch(function (error) {
      var described = describeFailure(error);
      if (error.status === 401) {
        clearToken('token 无效：' + (error.code || 'auth.invalid'));
        return;
      }
      dom['list-count'].textContent = '—';
      showState('list', described.kind, described.title, described.body);
    }).finally(function () {
      state.busy = false;
      dom['filter-apply'].disabled = false;
    });
  }

  function renderSessions() {
    var list = dom['session-list'];
    list.textContent = '';

    state.sessions.forEach(function (session) {
      var item = element('li', 'session-item');
      item.setAttribute('role', 'option');
      item.setAttribute('aria-selected', state.selected === session.sessionId ? 'true' : 'false');

      item.appendChild(element('div', 'session-source', session.sourceKey || session.sessionId));

      var sub = element('div', 'session-sub');
      sub.appendChild(element('span', null, (session.messageCount || 0) + ' 条消息'));
      sub.appendChild(element('span', null, '最近 ' + formatTime(session.lastMessageAt)));
      if (session.actorId) {
        sub.appendChild(element('span', null, 'actor ' + session.actorId));
      }
      item.appendChild(sub);

      if (session.queryScopeNotice) {
        item.appendChild(element('div', 'session-note', session.queryScopeNotice));
      }

      item.addEventListener('click', function () {
        selectSession(session);
      });

      list.appendChild(item);
    });
  }

  // ------------------------------------------------------------------ detail

  function selectSession(session) {
    state.selected = session.sessionId;
    state.messages = [];
    state.after = 0;
    renderSessions();

    dom['detail-empty'].hidden = true;
    dom['detail'].hidden = false;
    dom['detail-source'].textContent = session.sourceKey || session.sessionId;
    dom['detail-meta'].textContent =
      'session ' + session.sessionId +
      ' · ' + (session.messageCount || 0) + ' 条消息' +
      ' · 最近 ' + formatTime(session.lastMessageAt) +
      (session.actorId ? ' · actor ' + session.actorId : '');
    dom['message-list'].textContent = '';
    dom['detail-foot-count'].textContent = '加载中…';
    dom['detail-foot-loaded'].hidden = true;
    dom['message-more'].hidden = true;
    showState('detail', null);

    loadMessages(true);
  }

  function loadMessages(reset) {
    if (!state.selected) {
      return;
    }

    if (reset) {
      state.messages = [];
      state.after = 0;
      dom['message-list'].textContent = '';
    }

    dom['message-more'].disabled = true;

    api('/api/sessions/' + encodeURIComponent(state.selected) + '/messages', {
      after: state.after,
      limit: state.limits.maxMessagePageSize
    }).then(function (payload) {
      var batch = payload.items || [];
      state.messages = state.messages.concat(batch);
      state.after = payload.nextAfter || state.after;

      renderMessages();
      dom['detail-foot-count'].textContent = '已加载 ' + state.messages.length + ' 条消息';

      var reachedEnd = batch.length === 0;
      dom['message-more'].hidden = reachedEnd;
      dom['detail-foot-loaded'].hidden = !reachedEnd;

      if (state.messages.length === 0) {
        showState('detail', 'empty', '该会话没有消息', '宿主返回了空消息列表。');
      } else {
        showState('detail', null);
      }
    }).catch(function (error) {
      if (error.status === 401) {
        clearToken('token 无效：' + (error.code || 'auth.invalid'));
        return;
      }
      var described = describeFailure(error);
      showState('detail', described.kind, described.title, described.body);
    }).finally(function () {
      dom['message-more'].disabled = false;
    });
  }

  function renderMessages() {
    var list = dom['message-list'];
    list.textContent = '';

    state.messages.forEach(function (message) {
      var role = (message.role || 'unknown').toLowerCase();
      var item = element('li', 'message message-' + (role === 'user' || role === 'assistant' || role === 'system' ? role : 'system'));

      var head = element('div', 'message-head');
      head.appendChild(element('span', 'message-role', message.role || 'unknown'));
      head.appendChild(element('span', null, '#' + message.messageId));
      head.appendChild(element('span', null, formatTime(message.createdAt)));
      item.appendChild(head);

      item.appendChild(element('pre', 'message-body', message.text || ''));
      if (message.truncated) {
        item.appendChild(element('p', 'message-truncated', '已截断显示（上限 ' + state.limits.maxMessageCharacters + ' 字符）'));
      }

      list.appendChild(item);
    });
  }

  // ------------------------------------------------------------------ wiring

  function readFilters() {
    state.filters = {
      source: dom['filter-source'].value.trim(),
      actor: dom['filter-actor'].value.trim(),
      query: dom['filter-query'].value.trim(),
      since: dom['filter-since'].value ? new Date(dom['filter-since'].value).toISOString() : '',
      until: dom['filter-until'].value ? new Date(dom['filter-until'].value).toISOString() : ''
    };
  }

  function wire() {
    dom['token-form'].addEventListener('submit', function (event) {
      event.preventDefault();
      var token = dom['token-input'].value.trim();
      if (!token) {
        dom['token-message'].hidden = false;
        dom['token-message'].textContent = '先填入 console token。';
        return;
      }
      applyToken(token);
    });

    dom['token-forget'].addEventListener('click', function () {
      dom['token-input'].value = '';
      clearToken('已清除内存中的 token。');
    });

    dom['filters'].addEventListener('submit', function (event) {
      event.preventDefault();
      readFilters();
      if (state.selected) {
        state.selected = null;
        dom['detail'].hidden = true;
        dom['detail-empty'].hidden = false;
      }
      loadSessions(true);
    });

    dom['filter-reset'].addEventListener('click', function () {
      dom['filter-source'].value = '';
      dom['filter-actor'].value = '';
      dom['filter-query'].value = '';
      dom['filter-since'].value = '';
      dom['filter-until'].value = '';
      readFilters();
      loadSessions(true);
    });

    dom['list-refresh'].addEventListener('click', function () {
      loadSessions(true);
    });

    dom['list-more'].addEventListener('click', function () {
      loadSessions(false);
    });

    dom['message-more'].addEventListener('click', function () {
      loadMessages(false);
    });
  }

  document.addEventListener('DOMContentLoaded', function () {
    cacheDom();
    wire();
    dom['token-input'].focus();
  });
})();
