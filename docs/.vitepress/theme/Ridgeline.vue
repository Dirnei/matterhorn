<!--
  The dashboard's header ridge, ported to the docs landing as a full-bleed horizon
  under the hero. Same path and enzian tones as src/Matterhorn/wwwroot/index.html;
  the outline draws itself in on load (skipped under prefers-reduced-motion).
-->
<template>
  <svg
    class="mh-ridge"
    viewBox="0 0 1200 120"
    preserveAspectRatio="none"
    aria-hidden="true"
  >
    <path
      d="M0,120 L150,86 L250,98 L380,52 L470,74 L560,34 L610,50 L690,10 L742,46 L830,66 L940,40 L1050,78 L1200,58 L1200,120 Z"
      fill="var(--mh-ground)"
    />
    <path
      class="mh-ridge-line"
      d="M0,120 L150,86 L250,98 L380,52 L470,74 L560,34 L610,50 L690,10 L742,46 L830,66 L940,40 L1050,78 L1200,58"
      fill="none"
      stroke="var(--mh-enzian)"
      stroke-width="1.5"
      opacity="0.35"
    />
  </svg>
</template>
