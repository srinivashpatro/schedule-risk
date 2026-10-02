Project Risk Analysis - offline copy
====================================

1. Unzip this folder anywhere (for example your Documents folder).
2. Double-click Start-Offline.cmd.
3. Your browser opens the app at http://127.0.0.1:5180/ (the next free port if that one is taken).
   Keep the black window open while you work; close it to stop the app.

No internet connection is needed, and nothing is installed. The window runs a small web server
that only answers this computer (127.0.0.1); your XER files are opened in the browser and never leave it.

Why not just open index.html? Browsers refuse to start a WebAssembly app from a file:// address,
so the app needs to be served, even if only from your own computer.
