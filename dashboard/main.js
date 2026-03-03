'use strict'
const { app, BrowserWindow, ipcMain, dialog, shell } = require('electron')
const path = require('path')
const fs = require('fs')
const { spawn } = require('child_process')

// ── State ─────────────────────────────────────────────────────────────────
let mainWindow = null
let botProcess = null
let watchers = {}

// ── Data directory ────────────────────────────────────────────────────────
function findDataDir() {
  const candidates = [
    path.join(__dirname, '..', 'data'),
    path.join(app.getPath('userData'), 'data'),
  ]
  for (const d of candidates) {
    if (fs.existsSync(d)) return path.resolve(d)
  }
  return path.resolve(path.join(__dirname, '..', 'data'))
}

let dataDir = '' // set after app ready

function getBotRoot() {
  return path.dirname(dataDir)
}

function getConfigPath() {
  return path.join(getBotRoot(), 'polymarket_bot_config.json')
}

// ── Window ────────────────────────────────────────────────────────────────
function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1680,
    height: 980,
    minWidth: 1280,
    minHeight: 720,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
    },
    title: 'Polymarket Bot Dashboard',
    backgroundColor: '#0a0e1a',
    show: false,
    autoHideMenuBar: true,
  })
  mainWindow.loadFile(path.join(__dirname, 'index.html'))
  mainWindow.once('ready-to-show', () => mainWindow.show())
  mainWindow.on('closed', () => { mainWindow = null })
}

// ── File watcher ──────────────────────────────────────────────────────────
const watchDebounce = {}
function setupFileWatcher() {
  Object.values(watchers).forEach(w => { try { w.close() } catch {} })
  watchers = {}

  for (const file of ['portfolio.json', 'trades.jsonl', 'bot.log']) {
    const full = path.join(dataDir, file)
    const dir = path.dirname(full)
    if (!fs.existsSync(dir)) continue
    // Watch the directory so we catch newly created files too
    try {
      watchers[file] = fs.watch(dir, { persistent: false }, (evt, name) => {
        // On Windows, name can be null — fall back to watching any change in dir
        if ((name === null || name === file) && mainWindow) {
          clearTimeout(watchDebounce[file])
          watchDebounce[file] = setTimeout(() => {
            mainWindow.webContents.send('file-changed', file)
          }, 300)
        }
      })
    } catch {}
  }
}

// ── Safe JSON file read ───────────────────────────────────────────────────
function readJson(file) {
  try { return JSON.parse(fs.readFileSync(file, 'utf8')) } catch { return null }
}

function readLines(file, n) {
  try {
    const lines = fs.readFileSync(file, 'utf8').trim().split('\n').filter(Boolean)
    return lines.slice(-n).map(l => { try { return JSON.parse(l) } catch { return { level: 'INFO', message: l, timestamp: new Date().toISOString() } } })
  } catch { return [] }
}

// ── IPC ───────────────────────────────────────────────────────────────────
function setupIPC() {
  ipcMain.handle('get-data-dir', () => dataDir)

  ipcMain.handle('set-data-dir', (_, newDir) => {
    dataDir = path.resolve(newDir)
    setupFileWatcher()
    return dataDir
  })

  ipcMain.handle('browse-data-dir', async () => {
    const r = await dialog.showOpenDialog(mainWindow, {
      properties: ['openDirectory'],
      title: 'Select bot data directory (e.g. polymarket_bot/data)',
    })
    if (!r.canceled && r.filePaths.length) {
      dataDir = r.filePaths[0]
      setupFileWatcher()
      return dataDir
    }
    return null
  })

  ipcMain.handle('read-portfolio', () => {
    const f = path.join(dataDir, 'portfolio.json')
    if (!fs.existsSync(f)) return null
    return readJson(f)
  })

  ipcMain.handle('read-trades', () => {
    const f = path.join(dataDir, 'trades.jsonl')
    if (!fs.existsSync(f)) return []
    return readLines(f, 2000)
  })

  ipcMain.handle('read-logs', (_, n = 200) => {
    const f = path.join(dataDir, 'bot.log')
    if (!fs.existsSync(f)) return []
    return readLines(f, n)
  })

  // ── Config ───────────────────────────────────────────────────────────
  ipcMain.handle('read-config', () => {
    const f = getConfigPath()
    if (!fs.existsSync(f)) return {}
    return readJson(f) || {}
  })

  ipcMain.handle('write-config', (_, cfg) => {
    const f = getConfigPath()
    fs.writeFileSync(f, JSON.stringify(cfg, null, 2), 'utf8')
    return true
  })

  // ── Bot process ───────────────────────────────────────────────────────
  ipcMain.handle('bot-status', () => ({
    running: botProcess !== null && !botProcess.killed,
    pid: botProcess?.pid ?? null,
  }))

  ipcMain.handle('start-bot', (_, opts = {}) => {
    if (botProcess && !botProcess.killed) return { error: 'Already running' }

    const root = getBotRoot()
    let cmd, args, cwd, useShell = true

    if (opts.mode === 'dotnet') {
      const projDir = path.join(root, 'dotnet', 'PolymarketBot')
      const extraArgs = []
      if (opts.verbose) extraArgs.push('--verbose')
      if (opts.console) extraArgs.push('--console')

      // Prefer running the pre-compiled binary directly to avoid recompilation
      // locking PolymarketBot.exe while a previous instance is still running.
      const exeCandidates = [
        path.join(projDir, 'bin', 'Release', 'net8.0', 'PolymarketBot.exe'),
        path.join(projDir, 'bin', 'Debug',   'net8.0', 'PolymarketBot.exe'),
      ]
      const existingExe = exeCandidates.find(p => fs.existsSync(p))

      if (existingExe) {
        cmd = existingExe
        args = extraArgs
        cwd = projDir
        useShell = false  // direct exe path — shell:true would split at spaces in path
      } else {
        // First-ever run — must compile. Pass extra args after '--'.
        cmd = 'dotnet'
        args = ['run', ...(extraArgs.length ? ['--', ...extraArgs] : [])]
        cwd = projDir
      }
    } else {
      cmd = process.platform === 'win32' ? 'python' : 'python3'
      args = ['main.py']
      if (opts.verbose) args.push('--verbose')
      if (opts.console) args.push('--console')
      cwd = path.join(root, 'python')
    }

    // Rotate previous bot.log → bot-TIMESTAMP.log so each session is isolated
    try {
      const logFile = path.join(dataDir, 'bot.log')
      if (fs.existsSync(logFile) && fs.statSync(logFile).size > 0) {
        const ts = new Date().toISOString().slice(0, 19).replace(/T/, '_').replace(/:/g, '-')
        fs.renameSync(logFile, path.join(dataDir, `bot-${ts}.log`))
      }
    } catch {}

    try {
      botProcess = spawn(cmd, args, { cwd, shell: useShell, env: { ...process.env } })

      const fwd = (level) => (data) => {
        const msg = data.toString().trim()
        if (msg && mainWindow) mainWindow.webContents.send('bot-output', { level, message: msg, timestamp: new Date().toISOString() })
      }
      botProcess.stdout?.on('data', fwd('INFO'))
      botProcess.stderr?.on('data', fwd('WARNING'))

      botProcess.on('close', code => {
        botProcess = null
        if (mainWindow) mainWindow.webContents.send('bot-stopped', { code })
      })

      return { pid: botProcess.pid }
    } catch (e) {
      botProcess = null
      return { error: e.message }
    }
  })

  ipcMain.handle('stop-bot', () => {
    if (!botProcess || botProcess.killed) return { error: 'Not running' }
    botProcess.kill('SIGTERM')
    setTimeout(() => { if (botProcess && !botProcess.killed) botProcess.kill('SIGKILL') }, 3000)
    return { ok: true }
  })

  ipcMain.handle('open-logs-dir', () => {
    shell.openPath(dataDir)
  })

  ipcMain.handle('save-file', async (_, { content, defaultName }) => {
    const r = await dialog.showSaveDialog(mainWindow, {
      defaultPath: defaultName,
      filters: [
        { name: 'Log Files', extensions: ['txt', 'log'] },
        { name: 'All Files', extensions: ['*'] },
      ],
    })
    if (!r.canceled && r.filePath) {
      fs.writeFileSync(r.filePath, content, 'utf8')
      return { ok: true }
    }
    return { ok: false }
  })
}

// ── App lifecycle ─────────────────────────────────────────────────────────
app.whenReady().then(() => {
  dataDir = findDataDir()
  createWindow()
  setupIPC()
  setTimeout(setupFileWatcher, 800)
  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow()
  })
})

app.on('window-all-closed', () => {
  if (botProcess && !botProcess.killed) botProcess.kill()
  if (process.platform !== 'darwin') app.quit()
})
