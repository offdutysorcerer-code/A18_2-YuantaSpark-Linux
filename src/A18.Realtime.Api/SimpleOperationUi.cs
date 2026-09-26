namespace A18.Realtime.Api;

internal static class SimpleOperationUi
{
    public const string Html = """
<!doctype html>
<html lang="zh-Hant">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>A18_2 精簡操作</title>
<style>
:root{color-scheme:dark;--bg:#0b111b;--panel:#121c2a;--line:#27364b;--text:#dbe7f5;--muted:#8191a8;--accent:#54a8ff}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:13px/1.45 system-ui,"Microsoft JhengHei",sans-serif}
header{padding:12px 18px;border-bottom:1px solid var(--line);background:#0f1825}.brand{display:flex;align-items:center;gap:12px;flex-wrap:wrap}.brand h1{margin:0;font-size:17px}.tabs{display:flex;gap:4px;align-items:center}.tabs a{display:inline-block;padding:7px 11px;border:1px solid var(--line);border-radius:6px;text-decoration:none;color:var(--muted);background:#0b1420}.tabs a.active{color:#fff;border-color:#2787d5;background:#1769aa}.content{min-height:calc(100vh - 58px)}
</style>
</head>
<body>
<header>
  <div class="brand">
    <h1>A18_2</h1>
    <nav class="tabs" aria-label="A18_2 頁面">
      <a href="/swing">完整操作</a>
      <a href="/simple" class="active" aria-current="page">精簡操作</a>
    </nav>
  </div>
</header>
<main class="content"></main>
</body>
</html>
""";
}
