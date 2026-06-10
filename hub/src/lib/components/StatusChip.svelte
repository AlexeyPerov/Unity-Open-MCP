<script lang="ts">
  type Tone = "ok" | "warn" | "missing" | "running" | "info" | "muted";

  let {
    tone = "info",
    label,
    title = "",
  }: {
    tone?: Tone;
    label: string;
    title?: string;
  } = $props();
</script>

<span class="chip chip-{tone}" {title}>{label}</span>

<style>
  .chip {
    display: inline-flex;
    align-items: center;
    padding: 0.1rem 0.42rem;
    border-radius: 4px;
    font-size: 0.66rem;
    font-weight: 600;
    line-height: 1.35;
    letter-spacing: 0.01em;
    border: 1px solid transparent;
    white-space: nowrap;
  }

  .chip-ok {
    background: rgba(47, 111, 74, 0.18);
    color: #6fcf97;
    border-color: rgba(47, 111, 74, 0.45);
  }

  .chip-warn {
    background: rgba(201, 162, 39, 0.18);
    color: #e0bf5a;
    border-color: rgba(201, 162, 39, 0.45);
  }

  .chip-missing {
    background: rgba(222, 53, 118, 0.18);
    color: #f0a8c2;
    border-color: rgba(222, 53, 118, 0.5);
  }

  .chip-running {
    background: rgba(92, 124, 250, 0.18);
    color: #9bb3ff;
    border-color: rgba(92, 124, 250, 0.45);
  }

  .chip-info {
    background: rgba(110, 118, 140, 0.18);
    color: #b4b8c5;
    border-color: rgba(110, 118, 140, 0.45);
  }

  .chip-muted {
    background: rgba(110, 118, 140, 0.1);
    color: #6f7280;
    border-color: rgba(110, 118, 140, 0.3);
  }
</style>
