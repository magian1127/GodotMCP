// listen 探针:直发 subscriptions/listen,打印到达的事件。
const token = (await import('node:fs')).readFileSync(
  process.env.APPDATA + '\\godot-mcp-toolkit\\daemon-token', 'utf8').trim()
const response = await fetch('http://127.0.0.1:6590/', {
  method: 'POST',
  headers: {
    'Content-Type': 'application/json',
    Accept: 'application/json, text/event-stream',
    Authorization: `Bearer ${token}`,
    'MCP-Protocol-Version': '2026-07-28',
    'Mcp-Method': 'subscriptions/listen',
  },
  body: JSON.stringify({
    jsonrpc: '2.0', id: 'probe', method: 'subscriptions/listen',
    params: {
      notifications: { toolsListChanged: true },
      _meta: {
        'io.modelcontextprotocol/protocolVersion': '2026-07-28',
        'io.modelcontextprotocol/clientCapabilities': {},
        'io.modelcontextprotocol/clientInfo': { name: 'listen-probe', version: '1' },
      },
    },
  }),
})
console.log('status:', response.status)
console.log('content-type:', response.headers.get('content-type'))
const reader = response.body.getReader()
const decoder = new TextDecoder()
const deadline = Date.now() + 2500
;(async () => {
  setTimeout(async () => {
    // 2s 后触发一次真实变更(reset 一个已激活组)。
    await fetch('http://127.0.0.1:6590/', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Accept: 'application/json, text/event-stream',
        Authorization: `Bearer ${token}`,
        'MCP-Protocol-Version': '2025-11-25',
      },
      body: JSON.stringify({ jsonrpc: '2.0', id: 50, method: 'tools/call', params: {
        name: 'discover_tools', arguments: { request: ['audio'] } } }),
    })
    console.log('reset triggered')
  }, 1200)
  while (Date.now() < deadline) {
    const { done, value } = await reader.read()
    if (done) { console.log('stream done'); break }
    console.log('CHUNK:', decoder.decode(value, { stream: true }).slice(0, 300))
  }
})()
