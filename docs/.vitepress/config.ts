import { defineConfig } from 'vitepress'

export default defineConfig({
  title: 'Matterhorn',
  description: 'A neutral bridge that puts your Matter devices on MQTT — the Zigbee2MQTT of Matter.',
  base: '/matterhorn/',
  srcExclude: ['superpowers/**'],
  themeConfig: {
    nav: [
      { text: 'Guide', link: '/guide/getting-started' },
      { text: 'GitHub', link: 'https://github.com/dirnei/matterhorn' }
    ],
    socialLinks: [
      { icon: 'github', link: 'https://github.com/dirnei/matterhorn' }
    ]
  }
})
