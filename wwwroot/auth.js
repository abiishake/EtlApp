// Shared by every page: when the sign-in expires, API calls answer 401 -> go to the login page (and come back here after).
// Also wires the "Sign out" button when the page has one.
(() => {
  const toLogin = () => {
    const back = location.pathname + location.search;
    location.href = "/login" + (back === "/" ? "" : "?returnUrl=" + encodeURIComponent(back));
  };
  const realFetch = window.fetch.bind(window);
  window.fetch = async (...args) => {
    const res = await realFetch(...args);
    if (res.status === 401) toLogin();
    return res;
  };
  const wire = () => {
    const btn = document.getElementById("logoutBtn");
    if (!btn) return;
    btn.onclick = async () => {
      btn.disabled = true;
      try { await realFetch("/api/logout", { method: "POST" }); } catch { }
      location.href = "/login";
    };
  };
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", wire); else wire();
})();
