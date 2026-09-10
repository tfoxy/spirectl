import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const testDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(testDir, "..", "..");

function readRepoFile(path) {
  return readFileSync(resolve(repoRoot, path), "utf8");
}

test("repo skill packaging stays aligned across the skill and public docs", () => {
  const skill = readRepoFile("skills/spirectl/SKILL.md");
  const runtimeReference = readRepoFile("skills/spirectl/references/cli-runtime.md");
  const libraryReference = readRepoFile("skills/spirectl/references/library-integration.md");
  const aiSkillDoc = readRepoFile("docs/ai-skill.md");
  const currentStatus = readRepoFile("docs/current-status.md");
  const npmReadme = readRepoFile("npm-wrapper/README.md");
  const readme = readRepoFile("README.md");

  assert.match(skill, /^---\nname: spirectl\n/m);
  assert.match(skill, /^description: .+/m);
  assert.match(skill, /command -v sts2 >/);
  assert.match(skill, /npx @spirectl\/sts2/);
  assert.match(skill, /inspect ai-tools/);
  assert.match(skill, /inspect actions/);
  assert.match(skill, /inspect state-schema/);
  assert.match(skill, /references\/render-assets\.md/);
  assert.match(skill, /references\/cli-runtime\.md/);
  assert.match(skill, /semantic `act`/i);
  assert.match(skill, /dangerous mode/i);
  assert.match(skill, /Node wrapper/);
  assert.match(runtimeReference, /compact agent-first/);
  assert.match(runtimeReference, /fallback choose/i);
  assert.match(libraryReference, /sts2-mcp/);
  assert.match(libraryReference, /ISpirectlRuntime/);

  assert.match(aiSkillDoc, /npx skills add <owner>\/<repo> --skill spirectl/);
  assert.match(aiSkillDoc, /https:\/\/github\.com\/<owner>\/<repo>\/tree\/main\/skills\/spirectl/);
  assert.match(aiSkillDoc, /sts2 skill install/);

  assert.match(currentStatus, /repo-native self-contained skill pack/i);
  assert.match(currentStatus, /npx skills add <owner>\/<repo> --skill spirectl/);

  assert.match(npmReadme, /command -v sts2 >/);
  assert.match(npmReadme, /npx @spirectl\/sts2/);
  assert.match(npmReadme, /sts2-mcp/);
  assert.match(npmReadme, /sts2 skill install/);

  assert.match(readme, /skill/i);
  assert.match(readme, /npx skills add tfoxy\/spirectl --skill spirectl/);
  assert.match(readme, /docs\/ai-skill\.md/);
  assert.match(readme, /sts2 skill install/);
});
