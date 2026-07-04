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
    ],
    sidebar: {
      '/guide/': [
        {
          text: 'Guide',
          items: [
            { text: 'Getting started', link: '/guide/getting-started' },
            { text: 'How it works', link: '/guide/how-it-works' },
            { text: 'Commissioning a device', link: '/guide/commissioning' },
            { text: 'Controlling a device', link: '/guide/control' },
            { text: 'Configuration', link: '/guide/configuration' },
            { text: 'REST API', link: '/guide/rest-api' }
          ]
        }
      ]
    }
  }
})
