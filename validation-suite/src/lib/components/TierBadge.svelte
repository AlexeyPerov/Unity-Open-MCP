<script lang="ts">
  import type { RequirementLevel } from "@validation-suite/core";

  let { level, automated = false }: { level: RequirementLevel; automated?: boolean } = $props();

  const map: Record<RequirementLevel, { label: string; cls: string }> = {
    "required-core": { label: "Required · core", cls: "core" },
    "required-extended": { label: "Required · extended", cls: "extended" },
    optional: { label: "Optional", cls: "optional" },
  };
</script>

<span class="tier {map[level].cls}">{map[level].label}</span>
{#if automated}
  <span class="auto" title="This scenario has automated test coverage">Auto-covered</span>
{/if}

<style>
  .tier,
  .auto {
    display: inline-flex;
    align-items: center;
    padding: 2px 7px;
    border-radius: var(--radius-sm);
    font-size: 11px;
    font-weight: 500;
    white-space: nowrap;
  }
  .core {
    color: var(--accent);
    background: var(--accent-soft);
  }
  .extended {
    color: var(--warn);
    background: var(--warn-soft);
  }
  .optional {
    color: var(--text-dim);
    background: var(--bg-elev-2);
  }
  .auto {
    color: var(--ok);
    background: var(--ok-soft);
    margin-left: 4px;
  }
</style>
