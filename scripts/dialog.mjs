import fs from 'node:fs'

const file = process.argv[2]
const out = process.argv[3]
const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/).filter(Boolean)
const chunks = []

function texts (content) {
  if (typeof content === 'string') return content
  if (!Array.isArray(content)) return ''
  return content.filter((p) => p && p.type === 'text').map((p) => p.text || '').join('\n')
}

for (const line of lines) {
  let rec
  try { rec = JSON.parse(line) } catch { continue }
  const data = rec.data || {}
  if (rec.type === 'user/message') {
    let t = texts(data.content ?? data.message?.content ?? data.text)
    // drop injected runtime boilerplate
    const cut = t.indexOf('Current runtime context.')
    if (cut > 0) t = t.slice(0, cut)
    const cut2 = t.indexOf('<system-reminder>')
    if (cut2 > 0) t = t.slice(0, cut2)
    chunks.push(`\n\n===== USER (${new Date(rec.time).toISOString()}) =====\n${t.trim()}`)
  } else if (rec.type === 'assistant/message') {
    const t = texts(data.message?.content ?? data.content ?? data.text)
    const clean = t.trim()
    if (clean) chunks.push(`\n--- ASSISTANT ---\n${clean}`)
  } else if (rec.type === 'session/title') {
    chunks.push(`\n[topic] ${JSON.stringify(data).slice(0, 300)}`)
  }
}
fs.writeFileSync(out, chunks.join('\n'))
console.log('written', out, chunks.length)
