const fs = require('fs');
const path = 'D:\\Source\\ValleyAI\\packages\\core\\e2e\\stardew-run.ts';
let content = fs.readFileSync(path, 'utf8');

const oldCode = `// 检查第5轮：是否 set_state FOLLOW
    const t5 = turns[4];
    if (t5) {
      const hasFollow = t5.toolCalls.some(tc => tc.name === "set_state" && tc.args.state === "FOLLOW");
      log.push(\`- 第5轮（冒险邀请）: \${hasFollow ? "✅ set_state FOLLOW" : "⚠️ 未 set_state FOLLOW"}\\n\`);
    }`;

const newCode = `// 检查第5轮：冒险邀请回应（从角色角度评价自然度）
    const t5 = turns[4];
    if (t5) {
      const acceptsInvitation = t5.npcSpokenText.includes("好的") || t5.npcSpokenText.includes("明天") || t5.npcSpokenText.includes("见") || t5.npcSpokenText.includes("一起") || t5.npcSpokenText.includes("去");
      log.push(\`- 第5轮（冒险邀请）: \${acceptsInvitation ? "✅ 同意一起去矿洞" : "❌ 未回应邀请"} — "\${t5.npcSpokenText.slice(0, 50)}"\`);
    }`;

content = content.replace(oldCode, newCode);
fs.writeFileSync(path, content);
console.log('修改完成');
