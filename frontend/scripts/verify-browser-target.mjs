import fs from 'node:fs';
import path from 'node:path';
import { parseSync, transformSync } from 'rolldown/utils';

const TARGET = 'chrome79';
const FEATURES = ['optionalChaining', 'nullishCoalescing', 'logicalAssignment', 'classStaticBlock',
  'privateMethod', 'privateBrandCheck', 'topLevelAwait', 'regexpMatchIndices', 'regexpUnicodeSets',
  'importAttributes', 'explicitResourceManagement'];
const emptyCounts = () => Object.fromEntries(FEATURES.map(feature => [feature, 0]));
const message = value => value?.message ?? String(value);

/** 基于实际 AST 计数，字符串、注释中的 ?. 或 ?? 不应误判成浏览器语法。 */
function countUnsupportedSyntax(program) {
  const counts = emptyCounts();
  function visit(node, functionDepth = 0) {
    if (!node || typeof node !== 'object') return;
    if (node.type === 'ChainExpression') counts.optionalChaining++;
    if (node.type === 'LogicalExpression' && node.operator === '??') counts.nullishCoalescing++;
    if (node.type === 'AssignmentExpression' && ['??=', '||=', '&&='].includes(node.operator)) counts.logicalAssignment++;
    if (node.type === 'StaticBlock') counts.classStaticBlock++;
    if (node.type === 'MethodDefinition' && node.key?.type === 'PrivateIdentifier') counts.privateMethod++;
    if (node.type === 'BinaryExpression' && node.operator === 'in' && node.left?.type === 'PrivateIdentifier') counts.privateBrandCheck++;
    if ((node.type === 'AwaitExpression' || (node.type === 'ForOfStatement' && node.await)) && functionDepth === 0) counts.topLevelAwait++;
    if (node.type === 'Literal' && node.regex) {
      if (node.regex.flags.includes('d')) counts.regexpMatchIndices++;
      if (node.regex.flags.includes('v')) counts.regexpUnicodeSets++;
    }
    if ((['ImportDeclaration', 'ExportNamedDeclaration', 'ExportAllDeclaration'].includes(node.type) && node.attributes?.length) ||
      (node.type === 'ImportExpression' && node.options)) counts.importAttributes++;
    if (node.type === 'VariableDeclaration' && ['using', 'await using'].includes(node.kind)) counts.explicitResourceManagement++;
    const nextDepth = functionDepth + (['FunctionDeclaration', 'FunctionExpression', 'ArrowFunctionExpression'].includes(node.type) ? 1 : 0);
    for (const [key, value] of Object.entries(node)) {
      if (['start', 'end', 'loc'].includes(key)) continue;
      if (Array.isArray(value)) for (const child of value) visit(child, nextDepth);
      else if (value && typeof value === 'object') visit(value, nextDepth);
    }
  }
  visit(program);
  return counts;
}

function inspectSource(source, file, kind) {
  const sourceType = kind === 'inline-script' ? 'script' : 'module';
  const parsed = parseSync(file, source, { sourceType });
  const result = { file, kind, bytes: Buffer.byteLength(source), counts: countUnsupportedSyntax(parsed.program),
    parseErrors: parsed.errors.map(message), transformErrors: [], transformWarnings: [], transformedCounts: emptyCounts() };
  // 只在内存中用同一编译器验证 target 能正确降级；这一步不改写或“修好”待检查的实际产物。
  try {
    const transformed = transformSync(file, source, { target: TARGET, sourceType, lang: 'js', sourceMap: false });
    result.transformErrors = transformed.errors.map(message);
    result.transformWarnings = transformed.warnings.map(message);
    if (!transformed.errors.length) {
      const checked = parseSync(file, transformed.code, { sourceType });
      result.transformErrors.push(...checked.errors.map(message));
      result.transformedCounts = countUnsupportedSyntax(checked.program);
    }
  } catch (error) { result.transformErrors.push(message(error)); }
  return result;
}

function assertSupported(report) {
  const problems = [];
  for (const file of report.files) {
    for (const [feature, count] of Object.entries(file.counts)) if (count) problems.push(`${file.file}: ${feature}=${count}`);
    if (file.parseErrors.length) problems.push(`${file.file}: parseErrors=${file.parseErrors.length}`);
    if (file.transformErrors.length) problems.push(`${file.file}: transformErrors=${file.transformErrors.length}`);
    if (file.transformWarnings.length) problems.push(`${file.file}: transformWarnings=${file.transformWarnings.length}`);
    for (const [feature, count] of Object.entries(file.transformedCounts)) if (count) problems.push(`${file.file}: transformed ${feature}=${count}`);
  }
  if (problems.length) {
    const error = new Error(`Browser target ${TARGET} verification failed:\n${problems.join('\n')}`);
    error.report = report;
    throw error;
  }
  return report;
}

/** 内存源代码检查供回归测试使用；不能用转换结果替代对原始发布字节的检查。 */
export function verifyBrowserSource(source, file = 'source.js', kind = 'module') {
  const result = inspectSource(source, file, kind);
  return assertSupported({ target: TARGET, counts: result.counts, files: [result] });
}

/** 检查发布目录全部 JS 与 HTML 内联 JS；失败时附带 error.report，调用方不得发布或更新 latest。 */
export function verifyBrowserTarget(distPath) {
  const root = path.resolve(distPath);
  const report = { target: TARGET, jsFileCount: 0, inlineScriptCount: 0, counts: emptyCounts(), files: [] };
  function visit(directory) {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
      const absolute = path.join(directory, entry.name);
      if (entry.isDirectory()) { visit(absolute); continue; }
      if (!entry.isFile() || !/\.(?:js|html)$/i.test(entry.name)) continue;
      const relative = path.relative(root, absolute).split(path.sep).join('/');
      const source = fs.readFileSync(absolute, 'utf8');
      if (/\.js$/i.test(entry.name)) {
        report.files.push(inspectSource(source, relative, 'module'));
        report.jsFileCount++;
        continue;
      }
      let index = 0;
      for (const match of source.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script\s*>/gi)) {
        const attributes = match[1];
        if (/(?:^|\s)src\s*=/i.test(attributes)) continue;
        const typeMatch = attributes.match(/(?:^|\s)type\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+))/i);
        const type = (typeMatch?.[1] ?? typeMatch?.[2] ?? typeMatch?.[3] ?? '').toLowerCase();
        if (type && !['module', 'text/javascript', 'application/javascript'].includes(type)) continue;
        index++;
        const kind = type === 'module' ? 'inline-module' : 'inline-script';
        report.files.push(inspectSource(match[2], `${relative}#inline-${index}.js`, kind));
        report.inlineScriptCount++;
      }
    }
  }
  visit(root);
  if (!report.jsFileCount) throw new Error('Browser target verification requires actual JavaScript build outputs.');
  if (!fs.existsSync(path.join(root, 'index.html'))) throw new Error('Browser target verification requires index.html.');
  for (const file of report.files) for (const feature of FEATURES) report.counts[feature] += file.counts[feature];
  return assertSupported(report);
}
