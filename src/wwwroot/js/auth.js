window.conx = {
  login: async function (url, user, pwd) {
    try {
      const resp = await fetch(url, {
        method: 'POST',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ userName: user, password: pwd })
      });
      return { ok: resp.ok, status: resp.status, statusText: resp.statusText };
    } catch (e) {
      return { ok: false, status: 0, statusText: e.toString() };
    }
  },
  
  logout: async function (url) {
    try {
      // 发送注销请求到服务器
      const resp = await fetch(url, {
        method: 'POST',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
        body: ''
      });
      
      // 无论服务器响应如何，都清除客户端的认证信息
      // 清除所有可能的认证相关 Cookie（直接删除不成功的话）
      document.cookie = "ConXAuth=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/";
      document.cookie = "ConXAuth=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/; domain=" + window.location.hostname;
      
      // 清除本地存储中的任何认证信息
      localStorage.clear();
      sessionStorage.clear();
      
      return { ok: resp.ok, status: resp.status };
    } catch (e) {
      console.error('Logout error:', e);
      return { ok: false, status: 0 };
    }
  }
};