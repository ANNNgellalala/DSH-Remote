import fs from 'node:fs'

const file = process.argv[2]
const mode = process.argv[3] || 'digest'
const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/).filter(Boolean)

function textOfContent (content) {
  if (typeof content === 'string') return content
  if (!Array.isArray(content)) return ''
  return content.map((p) => {
    if (!p) return ''
    if (p.type === 'text') return p.text || ''
    if (p.type === 'tool-call') return `[tool:${p.name}] ${JSON.stringify(p.input ?? p.args ?? {}).slice(0, 300)}`
    if (p.type === 'tool-result') return `[tool-result] ${JSON.stringify(p.output ?? p.content ?? '').slice(0, 300)}`
    if (p.type === 'reasoning') return p.text ? `[reasoning] ${p.text.slice(0, 200)}` : ''
    return `[${p.type}]`
  }).filter(Boolean).join('\n')
}

if (mode === 'shapes') {
  const counts = new Map()
  for (const line of lines) {
    let rec
    try { rec = JSON.parse(line) } catch { continue }
    const key = rec.type || '?'
    counts.set(key, (counts.get(key) || 0) + 1)
  }
  console.log([...counts].map(([k, v]) => `${k}: ${v}`).join('\n'))
  const sample = JSON.parse(lines[2])
  console.log(JSON.stringify(sample, null, 2).slice(0, 3000))
  process.exit(0)
}

for (const line of lines) {
  let rec
  try { rec = JSON.parse(line) } catch { continue }
  const data = rec.data || {}
  if (rec.type === 'user/message' || rec.type === 'user') {
    const msg = data.message || data
    const role = msg.role || 'user'
    console.log(`\n=== USER (${rec.time || ''}) ===\n${textOfContent(msg.content ?? msg.text)}`)
  } else if (rec.type === 'assistant/message' || rec.type === 'assistant') {
    const msg = data.message || data
    const text = textOfContent(msg.content ?? msg.text)
    if (text.trim()) console.log(`\n--- ASSISTANT ---\n${text}`)
  } else if (rec.type === 'turn/start' || rec.type === 'turn/finish' || rec.type === 'compaction') {
    console.log(`\n[${rec.type}] ${JSON.stringify(data).slice(0, 200)}`)
  }
}
