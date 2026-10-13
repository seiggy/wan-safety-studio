import {themes as prismThemes} from 'prism-react-renderer';
import type {Config} from '@docusaurus/types';
import type * as Preset from '@docusaurus/preset-classic';

const repo = 'https://github.com/seiggy/wan-safety-studio';

const config: Config = {
  title: 'WAN Safety Studio',
  tagline: 'Generate safety and equipment-failure video, label it, train a detector, run it on a Jetson.',
  favicon: 'img/favicon.ico',
  future: {v4: true},
  url: 'https://docs.wan-safety-studio.invalid',
  baseUrl: '/',
  organizationName: 'seiggy',
  projectName: 'wan-safety-studio',
  onBrokenLinks: 'throw',
  markdown: {
    mermaid: true,
    hooks: {onBrokenMarkdownLinks: 'throw'},
  },
  themes: ['@docusaurus/theme-mermaid'],
  i18n: {defaultLocale: 'en', locales: ['en']},

  presets: [
    [
      'classic',
      {
        // Developer docs: how the platform is built and how to change it.
        docs: {
          path: 'docs',
          routeBasePath: 'developers',
          sidebarPath: './sidebars-developers.ts',
          editUrl: `${repo}/tree/main/docs-site/`,
        },
        blog: false,
        theme: {customCss: './src/css/custom.css'},
      } satisfies Preset.Options,
    ],
  ],

  plugins: [
    [
      // Deployment docs: how to host the platform in your own Azure subscription.
      '@docusaurus/plugin-content-docs',
      {
        id: 'deployment',
        path: 'deployment',
        routeBasePath: 'deployment',
        sidebarPath: './sidebars-deployment.ts',
        editUrl: `${repo}/tree/main/docs-site/`,
      },
    ],
  ],

  themeConfig: {
    colorMode: {respectPrefersColorScheme: true},
    navbar: {
      title: 'WAN Safety Studio',
      logo: {alt: 'WAN Safety Studio', src: 'img/logo.svg'},
      items: [
        {type: 'docSidebar', sidebarId: 'developers', position: 'left', label: 'Developers'},
        {type: 'docSidebar', docsPluginId: 'deployment', sidebarId: 'deployment', position: 'left', label: 'Deployment'},
        {href: repo, label: 'GitHub', position: 'right'},
      ],
    },
    footer: {
      style: 'dark',
      links: [
        {
          title: 'Documentation',
          items: [
            {label: 'For developers', to: '/developers/intro'},
            {label: 'For cloud and DevOps engineers', to: '/deployment/intro'},
          ],
        },
        {title: 'Source', items: [{label: 'GitHub', href: repo}]},
      ],
      copyright: `WAN Safety Studio. Built with Docusaurus.`,
    },
    prism: {
      theme: prismThemes.github,
      darkTheme: prismThemes.dracula,
      additionalLanguages: ['csharp', 'bash', 'powershell', 'hcl', 'json', 'python', 'docker', 'yaml'],
    },
  } satisfies Preset.ThemeConfig,
};

export default config;
