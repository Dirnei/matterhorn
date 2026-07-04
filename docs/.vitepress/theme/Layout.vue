<script setup>
import DefaultTheme from 'vitepress/theme'
import Ridgeline from './Ridgeline.vue'

const { Layout } = DefaultTheme
</script>

<template>
  <Layout>
    <!-- Alpine horizon under the hero, above the feature grid. Client-only: the
         decorative SVG animates on mount and needn't be server-rendered. -->
    <template #home-hero-after>
      <ClientOnly>
        <Ridgeline />
      </ClientOnly>
    </template>
  </Layout>
</template>
