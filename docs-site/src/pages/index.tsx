import type {ReactNode} from 'react';
import Link from '@docusaurus/Link';
import Layout from '@theme/Layout';
import styles from './index.module.css';

const paths = [
  {
    title: 'I build and change the platform',
    to: '/developers/intro',
    body: 'Architecture, domains, the generation pipeline, the Azure ML provider, the UI, and how to add your own model trainer.',
  },
  {
    title: 'I host it in Azure',
    to: '/deployment/intro',
    body: 'Which resources get created, how they connect, what each setting does, and how to deploy, update and stop the studio.',
  },
];

export default function Home(): ReactNode {
  return (
    <Layout title="WAN Safety Studio" description="Documentation for WAN Safety Studio">
      <header className={styles.hero}>
        <h1>WAN Safety Studio</h1>
        <p>Generate synthetic safety and equipment-failure video with Wan 2.2, label it, train an Apache-licensed detector, and check it on a Jetson Orin Nano.</p>
      </header>
      <main className={styles.paths}>
        {paths.map((path) => (
          <Link key={path.to} className={styles.card} to={path.to}>
            <h2>{path.title}</h2>
            <p>{path.body}</p>
          </Link>
        ))}
      </main>
    </Layout>
  );
}
