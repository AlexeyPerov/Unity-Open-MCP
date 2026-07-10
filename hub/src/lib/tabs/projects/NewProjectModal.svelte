<script lang="ts">
  import Button from "$lib/components/shell/Button.svelte";
  import Select from "$lib/components/shell/Select.svelte";
  import { settingsStore } from "$lib/state/settings.svelte";
  import { discoveryStore } from "$lib/state/discovery.svelte";
  import type { RenderPipeline } from "$lib/services/config";
  import type { ProjectsHandlers, ProjectsState } from "./state.ts";
  import { pipelineSupportedForVersion, isPackageFormValid, isNewProjectFormValid } from "./constants.ts";

  interface Props {
    state: ProjectsState;
    handlers: ProjectsHandlers;
  }
  let { state, handlers }: Props = $props();

  let pipelineSupported = $derived(pipelineSupportedForVersion(state.newProjectVersion));
</script>

{#if state.newProjectModalOpen}
  <!-- svelte-ignore a11y_click_events_have_key_events -->
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div
    class="newproj-overlay"
    role="dialog"
    tabindex="-1"
    aria-modal="true"
    aria-labelledby="newproj-modal-title"
    onclick={(e) => { if (e.target === e.currentTarget) handlers.closeNewProjectModal(); }}
    onkeydown={(e) => { if (e.key === "Escape" && !state.newProjectCreating) handlers.closeNewProjectModal(); }}
  >
    <div class="newproj-modal">
      <header class="newproj-header">
        <h2 id="newproj-modal-title" class="newproj-title">New project</h2>
        {#if !state.newProjectCreating}
          <button
            type="button"
            class="walkup-close"
            aria-label="Close new project"
            onclick={handlers.closeNewProjectModal}
          >
            ×
          </button>
        {/if}
      </header>

      <div class="newproj-body">
        <nav class="newproj-tabs">
          <button
            type="button"
            class="newproj-tab"
            class:active={state.newProjectMode === "project"}
            onclick={() => handlers.setNewProjectMode("project")}
            disabled={state.newProjectCreating}
          >Unity project</button>
          <button
            type="button"
            class="newproj-tab"
            class:active={state.newProjectMode === "package"}
            onclick={() => handlers.setNewProjectMode("package")}
            disabled={state.newProjectCreating}
          >UPM package</button>
        </nav>

        {#if state.newProjectMode === "project"}
        <p class="newproj-desc">
          Scaffold a fresh Unity project on disk and register it in
          Hub. The project will appear at the top of the list once
          the modal closes.
        </p>

        <section class="newproj-field">
          <label class="newproj-label" for="newproj-parent">Parent folder</label>
          <div class="newproj-input-row">
            <input
              id="newproj-parent"
              type="text"
              class="newproj-input"
              placeholder="/Users/you/Projects"
              value={state.newProjectParent}
              oninput={(e) => handlers.setNewProjectField("newProjectParent", (e.currentTarget as HTMLInputElement).value)}
              disabled={state.newProjectCreating}
            />
            <Button variant="secondary" onclick={handlers.pickNewProjectParent} disabled={state.newProjectCreating}>
              Browse…
            </Button>
          </div>
        </section>

        <section class="newproj-field">
          <label class="newproj-label" for="newproj-name">Project name</label>
          <input
            id="newproj-name"
            type="text"
            class="newproj-input"
            placeholder="MyGame"
            value={state.newProjectName}
            oninput={(e) => handlers.setNewProjectField("newProjectName", (e.currentTarget as HTMLInputElement).value)}
            disabled={state.newProjectCreating}
          />
        </section>

        <section class="newproj-field">
          <label class="newproj-label" for="newproj-version">Unity version</label>
          {#if discoveryStore.installations.length > 0}
          <Select
            id="newproj-version"
            options={[
              { value: "", label: "Select an installed version", disabled: true },
              ...discoveryStore.installations.map((i) => ({ value: i.version, label: i.version })),
            ]}
            value={state.newProjectVersion}
            onchange={(v) => handlers.setNewProjectField("newProjectVersion", v)}
            disabled={state.newProjectCreating}
            placeholder="Select an installed version"
          />
          {:else}
            <input
              id="newproj-version"
              type="text"
              class="newproj-input"
              placeholder="2022.3.48f1"
              value={state.newProjectVersion}
              oninput={(e) => handlers.setNewProjectField("newProjectVersion", (e.currentTarget as HTMLInputElement).value)}
              disabled={state.newProjectCreating}
            />
            <p class="newproj-hint">
              No Unity installations discovered — open the Unity Versions tab to scan,
              or type a version manually.
            </p>
          {/if}
        </section>

        <section class="newproj-field">
          <label class="newproj-label" for="newproj-pipeline">Render pipeline</label>
          <Select
            id="newproj-pipeline"
            options={[
              { value: "none", label: "None (Built-in)" },
              { value: "urp", label: "URP (Universal Render Pipeline)" + (!pipelineSupported ? " — requires Unity 2019.3+" : ""), disabled: !pipelineSupported },
              { value: "hdrp", label: "HDRP (High Definition Render Pipeline)" + (!pipelineSupported ? " — requires Unity 2019.3+" : ""), disabled: !pipelineSupported },
            ]}
            value={state.newProjectPipeline}
            onchange={(v) => handlers.setNewProjectField("newProjectPipeline", v as RenderPipeline)}
            disabled={state.newProjectCreating}
          />
          {#if !pipelineSupported}
            <p class="newproj-hint newproj-hint-warn">
              URP / HDRP require Unity 2019.3 or newer. Built-in is the
              only available option for the selected version.
            </p>
          {:else}
            <p class="newproj-hint">
              Selecting one writes the matching
              <code>Packages/manifest.json</code> entry.
            </p>
          {/if}
        </section>

        <section class="newproj-field">
          <label class="newproj-label" for="newproj-bundle">Bundle version</label>
          <input
            id="newproj-bundle"
            type="text"
            class="newproj-input"
            placeholder="0.1.0"
            value={state.newProjectBundleVersion}
            oninput={(e) => handlers.setNewProjectField("newProjectBundleVersion", (e.currentTarget as HTMLInputElement).value)}
            disabled={state.newProjectCreating}
          />
        </section>

        <section class="newproj-field">
          <span class="newproj-label">Template</span>
          <div class="newproj-template-row" role="radiogroup" aria-label="Template">
            <label class="newproj-template-option">
              <input
                type="radio"
                name="newproj-template"
                value="empty"
                checked={state.newProjectTemplateKind === "empty"}
                disabled={state.newProjectCreating}
                onchange={() => handlers.setNewProjectField("newProjectTemplateKind", "empty")}
              />
              <span>Empty</span>
              <span class="newproj-template-hint">Minimal scaffold (Assets/, ProjectSettings/, Packages/)</span>
            </label>
            <label
              class="newproj-template-option"
              class:newproj-template-disabled={!state.newProjectHubTemplatesAvailable}
              title={state.newProjectHubTemplatesAvailable
                ? "Pick a template from Unity Hub's downloaded templates"
                : "Unity Hub is not installed or no templates are downloaded"}
            >
              <input
                type="radio"
                name="newproj-template"
                value="hub-default"
                checked={state.newProjectTemplateKind === "hub-default"}
                disabled={!state.newProjectHubTemplatesAvailable || state.newProjectCreating}
                onchange={() => handlers.setNewProjectField("newProjectTemplateKind", "hub-default")}
              />
              <span>Hub default</span>
              <span class="newproj-template-hint">
                {#if state.newProjectHubTemplatesAvailable}
                  {#if state.newProjectHubTemplatesFolder}
                    <code title={state.newProjectHubTemplatesFolder}>{state.newProjectHubTemplatesFolder}</code>
                  {/if}
                {:else}
                  <em>Unity Hub is not installed or no templates are downloaded.</em>
                {/if}
              </span>
            </label>
            {#if state.newProjectTemplateKind === "hub-default" && state.newProjectHubTemplatesAvailable}
              <Select
                class="newproj-template-picker"
                options={state.newProjectHubTemplates.map((tpl) => ({
                  value: tpl.path,
                  label: tpl.name + (tpl.unityVersion ? ` (${tpl.unityVersion})` : ""),
                }))}
                value={state.newProjectHubTemplatePath}
                onchange={(v) => handlers.setNewProjectField("newProjectHubTemplatePath", v)}
                disabled={state.newProjectCreating}
              />
            {/if}
            <label class="newproj-template-option">
              <input
                type="radio"
                name="newproj-template"
                value="custom"
                checked={state.newProjectTemplateKind === "custom"}
                disabled={state.newProjectCreating}
                onchange={() => handlers.setNewProjectField("newProjectTemplateKind", "custom")}
              />
              <span>Custom folder…</span>
              <span class="newproj-template-hint">
                Pick any Unity project root on disk. Manage the saved list in
                <strong>Settings → Custom template folders</strong>.
              </span>
            </label>
            {#if state.newProjectTemplateKind === "custom"}
              <div class="newproj-input-row">
                <input
                  type="text"
                  class="newproj-input"
                  placeholder="/Users/you/UnityTemplates/Empty"
                  value={state.newProjectCustomTemplatePath}
                  oninput={(e) => handlers.setNewProjectField("newProjectCustomTemplatePath", (e.currentTarget as HTMLInputElement).value)}
                  disabled={state.newProjectCreating}
                />
                <Button variant="secondary" onclick={handlers.pickNewProjectCustomTemplate} disabled={state.newProjectCreating}>
                  Browse…
                </Button>
              </div>
              {#if state.newProjectCustomTemplatePath && settingsStore.current && !settingsStore.current.unityDiscovery.customTemplateFolders.includes(state.newProjectCustomTemplatePath)}
                <div class="newproj-save-hint">
                  Save this path to Settings so it appears in the
                  <strong>Custom template folders</strong> list for next time.
                  <Button
                    variant="secondary"
                    onclick={() => handlers.saveCustomTemplateToSettings(state.newProjectCustomTemplatePath)}
                    disabled={state.newProjectCreating}
                  >
                    Save to Settings
                  </Button>
                </div>
              {/if}
            {/if}
          </div>
        </section>
        {:else}
          <p class="newproj-desc">
            Scaffold a fresh UPM package on disk and register it in Hub.
            The package will appear at the top of the list once the modal
            closes, tracked as a <strong>Package</strong>.
          </p>

          <section class="newproj-field">
            <label class="newproj-label" for="pkg-parent">Parent folder</label>
            <div class="newproj-input-row">
              <input
                id="pkg-parent"
                type="text"
                class="newproj-input"
                placeholder="/Users/you/Projects"
                value={state.newProjectParent}
                oninput={(e) => handlers.setNewProjectField("newProjectParent", (e.currentTarget as HTMLInputElement).value)}
                disabled={state.newProjectCreating}
              />
              <Button variant="secondary" onclick={handlers.pickNewProjectParent} disabled={state.newProjectCreating}>
                Browse…
              </Button>
            </div>
          </section>

          <section class="newproj-field">
            <label class="newproj-label" for="pkg-name">Package name</label>
            <input
              id="pkg-name"
              type="text"
              class="newproj-input"
              placeholder="com.author.my-package"
              value={state.pkgName}
              oninput={(e) => handlers.setNewProjectField("pkgName", (e.currentTarget as HTMLInputElement).value)}
              disabled={state.newProjectCreating}
              spellcheck="false"
            />
          </section>

          <section class="newproj-field">
            <label class="newproj-label" for="pkg-display">Display name</label>
            <input
              id="pkg-display"
              type="text"
              class="newproj-input"
              placeholder="My Package"
              value={state.pkgDisplayName}
              oninput={(e) => handlers.setNewProjectField("pkgDisplayName", (e.currentTarget as HTMLInputElement).value)}
              disabled={state.newProjectCreating}
            />
          </section>

          <section class="newproj-field">
            <label class="newproj-label" for="pkg-version">Version</label>
            <input
              id="pkg-version"
              type="text"
              class="newproj-input"
              value={state.pkgVersion}
              oninput={(e) => handlers.setNewProjectField("pkgVersion", (e.currentTarget as HTMLInputElement).value)}
              disabled={state.newProjectCreating}
              spellcheck="false"
            />
          </section>

          <section class="newproj-field">
            <label class="newproj-label" for="pkg-unity">Unity version</label>
            <input
              id="pkg-unity"
              type="text"
              class="newproj-input"
              placeholder="2022.3"
              value={state.pkgUnity}
              oninput={(e) => handlers.setNewProjectField("pkgUnity", (e.currentTarget as HTMLInputElement).value)}
              disabled={state.newProjectCreating}
              spellcheck="false"
            />
          </section>

          <section class="newproj-field">
            <label class="newproj-label" for="pkg-desc">Description</label>
            <input
              id="pkg-desc"
              type="text"
              class="newproj-input"
              placeholder="What the package does"
              value={state.pkgDescription}
              oninput={(e) => handlers.setNewProjectField("pkgDescription", (e.currentTarget as HTMLInputElement).value)}
              disabled={state.newProjectCreating}
            />
          </section>

          <section class="newproj-field">
            <label class="newproj-label" for="pkg-keywords">Keywords (comma-separated)</label>
            <input
              id="pkg-keywords"
              type="text"
              class="newproj-input"
              placeholder="tool, utility"
              value={state.pkgKeywords}
              oninput={(e) => handlers.setNewProjectField("pkgKeywords", (e.currentTarget as HTMLInputElement).value)}
              disabled={state.newProjectCreating}
              spellcheck="false"
            />
          </section>

          <section class="newproj-field">
            <label class="newproj-label" for="pkg-author">Author name</label>
            <input
              id="pkg-author"
              type="text"
              class="newproj-input"
              value={state.pkgAuthorName}
              oninput={(e) => handlers.setNewProjectField("pkgAuthorName", (e.currentTarget as HTMLInputElement).value)}
              disabled={state.newProjectCreating}
            />
          </section>

          <section class="newproj-field">
            <label class="newproj-label" for="pkg-author-url">Author URL</label>
            <input
              id="pkg-author-url"
              type="text"
              class="newproj-input"
              placeholder="https://github.com/…"
              value={state.pkgAuthorUrl}
              oninput={(e) => handlers.setNewProjectField("pkgAuthorUrl", (e.currentTarget as HTMLInputElement).value)}
              disabled={state.newProjectCreating}
              spellcheck="false"
            />
          </section>

          <label class="checkbox-row">
            <input type="checkbox" checked={state.pkgIncludeExtras} disabled={state.newProjectCreating} onchange={(e) => handlers.setNewProjectField("pkgIncludeExtras", (e.currentTarget as HTMLInputElement).checked)} />
            <span>Include README.md, CHANGELOG.md, LICENSE.md, and Samples~/</span>
          </label>
        {/if}

        {#if state.newProjectError}
          <p class="newproj-error" role="alert">{state.newProjectError}</p>
          {#if state.newProjectOverwriteConfirm}
            <div class="newproj-overwrite">
              <Button
                variant="destructive"
                onclick={state.newProjectMode === "package" ? handlers.submitNewPackageOverwrite : handlers.submitNewProjectOverwrite}
                disabled={state.newProjectCreating}
              >
                {state.newProjectCreating ? "Replacing…" : "Overwrite existing folder"}
              </Button>
              <span class="newproj-overwrite-hint">
                This will delete the existing folder at
                <code>{state.newProjectOverwriteConfirm}</code> and replace it.
              </span>
            </div>
          {/if}
        {/if}
      </div>

      <footer class="newproj-footer">
        <Button variant="secondary" onclick={handlers.closeNewProjectModal} disabled={state.newProjectCreating}>
          Cancel
        </Button>
        <Button
          variant="primary"
          onclick={state.newProjectMode === "package" ? handlers.submitNewPackage : handlers.submitNewProject}
          disabled={state.newProjectCreating || (state.newProjectMode === "package" ? !isPackageFormValid(state.newProjectParent, state.pkgName) : !isNewProjectFormValid(state.newProjectParent, state.newProjectName, state.newProjectVersion, state.newProjectTemplateKind, state.newProjectHubTemplatePath, state.newProjectCustomTemplatePath))}
        >
          {state.newProjectCreating ? "Creating…" : (state.newProjectMode === "package" ? "Create package" : "Create project")}
        </Button>
      </footer>
    </div>
  </div>
{/if}
