import { ArrowCounterClockwise, Briefcase, ClockAfternoon, Code, GameController, ListChecks, MagnifyingGlass, Plus, Sparkle, X } from '@phosphor-icons/react';
import { useEffect, useState } from 'react';
import type { AppPreferences, OrganizationRule, OrganizationRuleMatch } from '../types';
import { nativeWindow, parseNativeConfig, type HistorySummary, type OrganizationPreviewRow } from '../services/nativeDesktop';

interface AdvancedSettingsProps {
  preferences: AppPreferences;
  onChange: (preferences: AppPreferences) => void;
  onOrganizeNow: () => void;
  onUndoOrganization: () => void;
  onRedoOrganization: () => void;
}

interface RuleDraft {
  name: string;
  match: OrganizationRuleMatch;
  pattern: string;
  categoryId: string;
}

const emptyHistory: HistorySummary = { undoCount: 0, redoCount: 0, batches: [] };

export function AdvancedSettings({ preferences, onChange, onOrganizeNow, onUndoOrganization, onRedoOrganization }: AdvancedSettingsProps) {
  const [draft, setDraft] = useState<RuleDraft>({ name: '', match: 'extension', pattern: '', categoryId: preferences.categories[0]?.id ?? '' });
  const [preview, setPreview] = useState<OrganizationPreviewRow[]>([]);
  const [history, setHistory] = useState<HistorySummary>(emptyHistory);
  const [workspaceName, setWorkspaceName] = useState('');
  const [status, setStatus] = useState('');
  const [loadingPreview, setLoadingPreview] = useState(false);

  const reloadPreferences = async () => {
    if (!nativeWindow.loadConfig) return;
    try {
      const config = parseNativeConfig(await nativeWindow.loadConfig());
      if (config?.preferences) onChange(config.preferences);
    } catch {
      // 配置动作已经由宿主完成，刷新失败不阻塞设置页继续使用。
    }
  };

  const refreshHistory = async () => {
    try { setHistory(await nativeWindow.historySummary()); } catch { setHistory(emptyHistory); }
  };

  useEffect(() => { void refreshHistory(); }, []);

  const addRule = () => {
    const pattern = draft.pattern.trim();
    if (!pattern || !draft.categoryId) {
      setStatus('请填写匹配内容并选择目标分区');
      return;
    }
    const next: OrganizationRule = {
      id: `rule-${Date.now().toString(36)}`,
      name: draft.name.trim() || pattern,
      enabled: true,
      match: draft.match,
      pattern,
      categoryId: draft.categoryId,
      priority: preferences.rules.length + 1,
    };
    onChange({ ...preferences, rules: [...preferences.rules, next] });
    setDraft({ ...draft, name: '', pattern: '' });
    setStatus('规则已加入，按优先级从上到下匹配');
  };

  const updateRule = <K extends keyof OrganizationRule>(id: string, key: K, value: OrganizationRule[K]) => {
    onChange({ ...preferences, rules: preferences.rules.map((rule) => rule.id === id ? { ...rule, [key]: value } : rule) });
  };

  const removeRule = (id: string) => {
    const rules = preferences.rules.filter((rule) => rule.id !== id).map((rule, index) => ({ ...rule, priority: index + 1 }));
    onChange({ ...preferences, rules });
  };

  const loadPreview = async () => {
    setLoadingPreview(true);
    try {
      setPreview(await nativeWindow.organizationPreview());
      setStatus('预览已更新，执行收纳前会再次确认');
    } catch {
      setStatus('预览暂时不可用，请先完成一次桌面扫描');
    } finally {
      setLoadingPreview(false);
    }
  };

  const runCommand = async (command: string, message: string, reload = true) => {
    try {
      await nativeWindow.runManagerCommand(command);
      if (reload) await reloadPreferences();
      setStatus(message);
      await refreshHistory();
    } catch {
      setStatus('操作没有完成，请稍后重试');
    }
  };

  const saveWorkspace = async () => {
    const name = workspaceName.trim();
    if (!name) {
      setStatus('请先填写工作区名称');
      return;
    }
    await runCommand(`workspace-save:${name}`, `工作区“${name}”已保存`);
    setWorkspaceName('');
  };

  const importConfig = async () => {
    // DeskBox 约定：恢复配置是显式操作，覆盖前先确认
    if (!window.confirm('导入会覆盖当前的全部分类、规则与布局，继续吗？')) return;
    const path = await nativeWindow.importConfigFile();
    if (path) {
      await reloadPreferences();
      setStatus('配置已导入');
    }
  };

  const exportConfig = async () => {
    const path = await nativeWindow.exportConfig();
    if (path) setStatus('配置已导出');
  };

  return (
    <div className="advanced-settings">
      {/* 规则自动归类 */}
      <section className="settings-section">
        <div className="settings-section__heading">
          <ListChecks size={18} />
          <div>
            <strong>规则自动归类</strong>
            <small>按扩展名、文件名或完整路径匹配；序号越小优先级越高</small>
          </div>
        </div>

        {/* 响应式两段式规则编辑卡片 */}
        <div className="rule-editor">
          <div className="rule-editor__primary">
            <div className="rule-editor__field rule-editor__field--match">
              <label className="rule-editor__label">匹配方式</label>
              <select
                className="advanced-input"
                value={draft.match}
                onChange={(event) => setDraft({ ...draft, match: event.target.value as OrganizationRuleMatch })}
              >
                <option value="extension">扩展名</option>
                <option value="name">文件名</option>
                <option value="path">完整路径</option>
              </select>
            </div>

            <div className="rule-editor__field rule-editor__field--pattern">
              <label className="rule-editor__label">匹配内容</label>
              <input
                className="advanced-input"
                value={draft.pattern}
                placeholder={
                  draft.match === 'extension'
                    ? '扩展名用逗号隔开，如：psd, ai, sketch'
                    : draft.match === 'name'
                    ? '输入文件名通配符，如：*report* 或 temp*'
                    : '输入完整路径通配符，如：*Downloads* 或 *Desktop/Temp*'
                }
                onChange={(event) => setDraft({ ...draft, pattern: event.target.value })}
                onKeyDown={(event) => { if (event.key === 'Enter') addRule(); }}
              />
            </div>

            <div className="rule-editor__field rule-editor__field--category">
              <label className="rule-editor__label">收纳目标分区</label>
              <select
                className="advanced-input"
                value={draft.categoryId}
                onChange={(event) => setDraft({ ...draft, categoryId: event.target.value })}
              >
                {preferences.categories.map((category) => (
                  <option key={category.id} value={category.id}>{category.name}</option>
                ))}
              </select>
            </div>
          </div>

          <div className="rule-editor__secondary">
            <div className="rule-editor__field rule-editor__field--name">
              <label className="rule-editor__label">规则备注名称（可选）</label>
              <input
                className="advanced-input"
                value={draft.name}
                placeholder="给规则起个易记的名称（留空则默认以匹配内容命名）"
                onChange={(event) => setDraft({ ...draft, name: event.target.value })}
                onKeyDown={(event) => { if (event.key === 'Enter') addRule(); }}
              />
            </div>
            <button type="button" className="button button--primary rule-editor__add" onClick={addRule}>
              <Plus size={15} weight="bold" />
              <span>添加规则</span>
            </button>
          </div>
        </div>

        {/* 规则清单 */}
        <div className="rule-list">
          {preferences.rules.length === 0 ? (
            <div className="rule-empty-state">
              <p className="settings-note">还没有自定义规则。默认分类仍会按分区扩展名设置匹配；添加自定义规则后将优先按上方列表顺序自上而下匹配。</p>
            </div>
          ) : (
            preferences.rules.map((rule, index) => (
              <div className={`rule-row${rule.enabled ? '' : ' is-disabled'}`} key={rule.id}>
                <span className="rule-row__priority" title={`优先级 ${index + 1}`}>{index + 1}</span>
                <div className="rule-row__body">
                  <div className="rule-row__field rule-row__field--name">
                    <input className="advanced-input" value={rule.name} onChange={(event) => updateRule(rule.id, 'name', event.target.value)} placeholder="规则名称" aria-label="规则名称" />
                  </div>
                  <div className="rule-row__field rule-row__field--match">
                    <select className="advanced-input" value={rule.match} onChange={(event) => updateRule(rule.id, 'match', event.target.value as OrganizationRuleMatch)}>
                      <option value="extension">扩展名</option>
                      <option value="name">文件名</option>
                      <option value="path">路径</option>
                    </select>
                  </div>
                  <div className="rule-row__field rule-row__field--pattern">
                    <input className="advanced-input" value={rule.pattern} onChange={(event) => updateRule(rule.id, 'pattern', event.target.value)} placeholder="匹配规则" aria-label="匹配模式" />
                  </div>
                  <div className="rule-row__field rule-row__field--category">
                    <select className="advanced-input" value={rule.categoryId} onChange={(event) => updateRule(rule.id, 'categoryId', event.target.value)}>
                      {preferences.categories.map((category) => (
                        <option key={category.id} value={category.id}>{category.name}</option>
                      ))}
                    </select>
                  </div>
                </div>
                <div className="rule-row__actions">
                  <button type="button" className={`rule-toggle${rule.enabled ? ' is-active' : ''}`} onClick={() => updateRule(rule.id, 'enabled', !rule.enabled)}>
                    {rule.enabled ? '启用' : '停用'}
                  </button>
                  <button type="button" className="icon-button rule-row__remove" aria-label="删除规则" title="删除规则" onClick={() => removeRule(rule.id)}>
                    <X size={14} />
                  </button>
                </div>
              </div>
            ))
          )}
        </div>

        {/* 整理与预览动作 */}
        <div className="advanced-actions">
          <button type="button" className="settings-command advanced-action-btn" onClick={() => void loadPreview()} disabled={loadingPreview}>
            <div className="advanced-action-btn__icon"><MagnifyingGlass size={16} /></div>
            <div className="advanced-action-btn__text">
              <span>{loadingPreview ? '正在生成整理预览…' : '执行前查看整理预览'}</span>
              <small>只列出本次会被移动的项目，不会立即改动文件</small>
            </div>
          </button>
          <button type="button" className="settings-command advanced-action-btn" onClick={onOrganizeNow} disabled={preferences.organizeMode === 'reference'}>
            <div className="advanced-action-btn__icon"><Sparkle size={16} /></div>
            <div className="advanced-action-btn__text">
              <span>确认后执行收纳</span>
              <small>执行前仍会弹出项目清单确认并可撤销</small>
            </div>
          </button>
        </div>

        {preview.length > 0 && (
          <div className="preview-list">
            <div className="preview-list__heading">预览待整理项目（共 {preview.length} 个）</div>
            <div className="preview-list__items">
              {preview.slice(0, 24).map((row) => (
                <div className="preview-row" key={`${row.source}-${row.destination}`}>
                  <span title={row.name}>{row.name}</span>
                  <small>{row.category} · {row.ruleMatched ? '自定义规则' : '默认分类'}</small>
                </div>
              ))}
            </div>
            {preview.length > 24 && <p className="settings-note">还有 {preview.length - 24} 个项目未完全展开。</p>}
          </div>
        )}
      </section>

      {/* 批量操作与历史 */}
      <section className="settings-section">
        <div className="settings-section__heading">
          <ClockAfternoon size={18} />
          <div>
            <strong>批量操作与历史</strong>
            <small>面板内按住 Ctrl 多选项目，右键可批量收纳或恢复</small>
          </div>
        </div>
        <div className="history-actions">
          <div className="history-actions__buttons">
            <button type="button" className="button button--quiet" onClick={() => { onUndoOrganization(); window.setTimeout(() => void refreshHistory(), 400); }} disabled={history.undoCount === 0}>
              <ArrowCounterClockwise size={13} />
              <span>撤销</span>
            </button>
            <button type="button" className="button button--quiet" onClick={() => { onRedoOrganization(); window.setTimeout(() => void refreshHistory(), 400); }} disabled={history.redoCount === 0}>
              <span>恢复</span>
            </button>
            <button type="button" className="button button--quiet" onClick={() => void refreshHistory()}>
              <span>刷新历史</span>
            </button>
          </div>
          <div className="history-count-pill">
            <span>可撤销 <b>{history.undoCount}</b> 批</span>
            <span className="history-count-pill__sep">·</span>
            <span>可恢复 <b>{history.redoCount}</b> 批</span>
          </div>
        </div>
        {history.batches.length > 0 && (
          <div className="history-list">
            {history.batches.slice(-8).reverse().map((batch) => (
              <span className="history-badge" key={batch.index}>第 {batch.index} 批 · {batch.count} 项</span>
            ))}
          </div>
        )}
      </section>

      {/* 工作区预设 */}
      <section className="settings-section">
        <div className="settings-section__heading">
          <Sparkle size={18} />
          <div>
            <strong>工作区预设</strong>
            <small>不同场景适配不同桌面分区密度，并可保存自定义布局快照</small>
          </div>
        </div>
        <div className="workspace-presets">
          <button type="button" className="preset-card workspace-preset-card" onClick={() => void runCommand('workspace-preset:office', '已切换到办公工作区')}>
            <div className="workspace-preset-card__icon"><Briefcase size={22} weight="duotone" /></div>
            <div className="workspace-preset-card__info">
              <strong>办公</strong>
              <small>均衡排列，保留桌面中心开阔视野</small>
            </div>
          </button>
          <button type="button" className="preset-card workspace-preset-card" onClick={() => void runCommand('workspace-preset:game', '已切换到游戏工作区')}>
            <div className="workspace-preset-card__icon"><GameController size={22} weight="duotone" /></div>
            <div className="workspace-preset-card__info">
              <strong>游戏</strong>
              <small>集中收纳到右侧，减少中央遮挡</small>
            </div>
          </button>
          <button type="button" className="preset-card workspace-preset-card" onClick={() => void runCommand('workspace-preset:development', '已切换到开发工作区')}>
            <div className="workspace-preset-card__icon"><Code size={22} weight="duotone" /></div>
            <div className="workspace-preset-card__info">
              <strong>开发</strong>
              <small>等宽多列排布，列表展示更详尽</small>
            </div>
          </button>
        </div>
        <div className="workspace-save">
          <input
            className="advanced-input workspace-save__input"
            value={workspaceName}
            placeholder="保存当前布局为快照，例如：双屏工作区"
            onChange={(event) => setWorkspaceName(event.target.value)}
            onKeyDown={(event) => { if (event.key === 'Enter') void saveWorkspace(); }}
          />
          <button type="button" className="button button--primary workspace-save__submit" onClick={() => void saveWorkspace()}>
            <Plus size={14} />
            <span>保存快照</span>
          </button>
        </div>
        {preferences.workspaces.length > 0 && (
          <div className="workspace-list">
            {preferences.workspaces.map((workspace) => (
              <div className={`workspace-row${workspace.id === preferences.activeWorkspaceId ? ' is-active' : ''}`} key={workspace.id}>
                <span>
                  <strong>{workspace.name}</strong>
                  <small>{workspace.id === preferences.activeWorkspaceId ? '当前使用的工作区' : '已保存的布局快照'}</small>
                </span>
                <div className="workspace-row__actions">
                  <button type="button" className="button button--quiet" onClick={() => void runCommand(`workspace-activate:${workspace.id}`, `已切换到工作区“${workspace.name}”`)}>
                    切换
                  </button>
                  <button type="button" className="icon-button" aria-label={`删除${workspace.name}`} onClick={() => { if (window.confirm(`删除工作区“${workspace.name}”？`)) void runCommand(`workspace-delete:${workspace.id}`, '工作区已删除'); }}>
                    <X size={14} />
                  </button>
                </div>
              </div>
            ))}
          </div>
        )}
      </section>

      {/* 快速搜索与配置备份 */}
      <section className="settings-section">
        <div className="settings-section__heading">
          <MagnifyingGlass size={18} />
          <div>
            <strong>快速搜索与配置备份</strong>
            <small>全局快捷键 Ctrl + Alt + F；面板搜索按钮用于当前分区筛选</small>
          </div>
        </div>
        <div className="backup-actions">
          <button type="button" className="button button--quiet backup-btn" onClick={() => void exportConfig()}>
            导出配置与布局 (JSON)
          </button>
          <button type="button" className="button button--quiet backup-btn" onClick={() => void importConfig()}>
            导入配置与布局
          </button>
        </div>
        <p className="settings-note">
          导出的 JSON 包含分类规则、面板位置尺寸、工作区、收藏、最近使用和显示选项；文件仅保存在你指定的安全路径。
        </p>
        {status && <div className="settings-status-banner">{status}</div>}
      </section>
    </div>
  );
}
