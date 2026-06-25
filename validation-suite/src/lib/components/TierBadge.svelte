<script lang="ts">
  import type { RequirementLevel } from "@validation-suite/core";

  let { level, automated = false }: { level: RequirementLevel; automated?: boolean } = $props();

  const map: Record<RequirementLevel, { label: string; cls: string }> = {
    "required-core": { label: "Required · core", cls: "tier-core" },
    "required-extended": { label: "Required · extended", cls: "tier-extended" },
    optional: { label: "Optional", cls: "tier-optional" },
  };
</script>

<span class="tier {map[level].cls}">{map[level].label}</span>
{#if automated}
  <span class="tier tier-auto" title="This scenario has automated test coverage">Auto</span>
{/if}

<style>
  .tier {
    display: inline-flex;
    align-items: center;
    padding: 0.1rem 0.45rem;
    border-radius: 4px;
    font-size: 0.68rem;
    font-weight: 600;
    line-height: 1.3;
    white-space: nowrap;
  }

  .tier-core {
    background: var(--hub-info-bg);
    color: var(--hub-info-fg);
    border: 1px solid var(--hub-info-fg);
  }

  .tier-extended {
    background: var(--hub-warn-bg);
    color: var(--hub-warn-fg);
    border: 1px solid var(--hub-warn-fg);
  }

  .tier-optional {
    background: var(--hub-selected);
    color: var(--hub-text-muted);
    border: 1px solid var(--hub-border-light);
  }

  .tier-auto {
    background: var(--hub-warn-bg);
    color: var(--hub-warn-fg);
    border: 1px solid var(--hub-warn-fg);
    margin-left: 0.3rem;
  }
</style>
