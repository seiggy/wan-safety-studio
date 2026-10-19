import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { applyAppearance, loadAppearance } from './lib/appearance';
import './styles/app.css';

// Before the first render, so a pinned theme or accent never flashes the default.
applyAppearance(loadAppearance());

const root = document.getElementById('root');
if (!root) throw new Error('Missing #root element.');

createRoot(root).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
