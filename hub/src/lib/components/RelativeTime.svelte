<script lang="ts">
  let {
    iso,
    fallback = "—",
  }: {
    iso?: string;
    fallback?: string;
  } = $props();

  function parseIso(value?: string): Date | null {
    if (!value) return null;
    const d = new Date(value);
    if (Number.isNaN(d.getTime())) return null;
    return d;
  }

  function formatRelative(value?: string): string {
    const d = parseIso(value);
    if (!d) return fallback;
    const now = Date.now();
    const diffMs = now - d.getTime();
    const future = diffMs < 0;
    const abs = Math.abs(diffMs);
    const sec = Math.round(abs / 1000);
    const min = Math.round(sec / 60);
    const hr = Math.round(min / 60);
    const day = Math.round(hr / 24);
    const week = Math.round(day / 7);
    const month = Math.round(day / 30);
    const year = Math.round(day / 365);

    let core: string;
    if (sec < 45) core = "just now";
    else if (min < 60) core = `${min}m`;
    else if (hr < 24) core = `${hr}h`;
    else if (day < 7) core = `${day}d`;
    else if (week < 5) core = `${week}w`;
    else if (month < 12) core = `${month}mo`;
    else core = `${year}y`;

    if (core === "just now") return core;
    return future ? `in ${core}` : `${core} ago`;
  }

  let display = $derived(formatRelative(iso));
  let full = $derived.by(() => {
    const d = parseIso(iso);
    if (!d) return "";
    try {
      return d.toLocaleString();
    } catch {
      return d.toISOString();
    }
  });
</script>

<span class="rel" title={full}>{display}</span>

<style>
  .rel {
    font-size: 0.78rem;
    color: #b4b6c2;
    white-space: nowrap;
  }
</style>
