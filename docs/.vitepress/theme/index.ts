// Matterhorn docs theme: the default VitePress theme dressed in the dashboard's
// "alpine bridge station" identity. All visual work lives in custom.css — palette
// and type are lifted from src/Matterhorn/wwwroot/index.html.
import DefaultTheme from 'vitepress/theme'
import Layout from './Layout.vue'
import './custom.css'

export default {
  ...DefaultTheme,
  Layout
}
