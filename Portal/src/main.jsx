import React from 'react';
import { createRoot } from 'react-dom/client';
import './styles.css';

const cards = [
  ['Pending master-data review', 'Approval-gated records awaiting a planner decision'],
  ['Today\'s planning', 'Orders, loads, drivers and vehicles will appear here'],
  ['Exceptions', 'Missing data, duplicate and capacity issues'],
  ['Fleet status', 'DOT tracking and availability overview']
];

function App() {
  return <main>
    <header><div><span className="eyebrow">STUART LYONS HAULAGE</span><h1>Transport Management System</h1></div><button>Sign in</button></header>
    <section className="hero"><div><span className="eyebrow">OPERATIONS PORTAL</span><h2>Plan with control.</h2><p>The operational workspace for planning, master data and exceptions. Live records remain protected by approval rules.</p></div><div className="status"><strong>API connected</strong><span>Master-data promotion requires approval</span></div></section>
    <section className="grid">{cards.map(([title, text]) => <article key={title}><h3>{title}</h3><p>{text}</p><button className="link">Open</button></article>)}</section>
    <section className="queue"><div><span className="eyebrow">MASTER DATA</span><h2>Review queue</h2><p>Pending imports will be displayed here after Entra sign-in is connected.</p></div><button>Open review queue</button></section>
  </main>
}
createRoot(document.getElementById('root')).render(<App />);
