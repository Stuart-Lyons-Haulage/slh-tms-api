import React from 'react';
import { createRoot } from 'react-dom/client';
import { MsalProvider, useMsal } from '@azure/msal-react';
import { msalInstance, loginRequest } from './auth';
import { getPendingStaging } from './tmsApi';
import './styles.css';

const cards = [
  ['Pending master-data review', 'Approval-gated records awaiting a planner decision'],
  ['Today\'s planning', 'Orders, loads, drivers and vehicles will appear here'],
  ['Exceptions', 'Missing data, duplicate and capacity issues'],
  ['Fleet status', 'DOT tracking and availability overview']
];

function App() {
  const { instance, accounts } = useMsal();
  const [queue, setQueue] = React.useState([]);
  const [message, setMessage] = React.useState('Sign in to load the review queue.');
  const account = accounts[0];

  async function signIn() {
    try { await instance.loginRedirect(loginRequest); }
    catch { setMessage('Sign-in could not be started. Please contact the TMS administrator.'); }
  }

  async function loadQueue() {
    try {
      setMessage('Loading pending records…');
      const records = await getPendingStaging(instance, account);
      setQueue(records);
      setMessage(records.length ? `${records.length} pending records loaded.` : 'No master-data records are awaiting review.');
    } catch (error) { setMessage(error.message); }
  }

  return <main>
    <header><div><span className="eyebrow">STUART LYONS HAULAGE</span><h1>Transport Management System</h1></div><button onClick={account ? loadQueue : signIn}>{account ? 'Refresh queue' : 'Sign in'}</button></header>
    <section className="hero"><div><span className="eyebrow">OPERATIONS PORTAL</span><h2>Plan with control.</h2><p>The operational workspace for planning, master data and exceptions. Live records remain protected by approval rules.</p></div><div className="status"><strong>API connected</strong><span>Master-data promotion requires approval</span></div></section>
    <section className="grid">{cards.map(([title, text]) => <article key={title}><h3>{title}</h3><p>{text}</p><button className="link">Open</button></article>)}</section>
    <section className="queue"><div><span className="eyebrow">MASTER DATA</span><h2>Review queue</h2><p>{message}</p>{queue.length > 0 && <ul>{queue.slice(0, 5).map(item => <li key={item.id}>{item.entityType} · received {new Date(item.receivedAtUtc).toLocaleString('en-GB')}</li>)}</ul>}</div><button onClick={account ? loadQueue : signIn}>{account ? 'Refresh queue' : 'Sign in'}</button></section>
  </main>
}

const root = createRoot(document.getElementById('root'));

async function bootstrap() {
  if (msalInstance) {
    await msalInstance.initialize();
  }

  root.render(msalInstance
    ? <MsalProvider instance={msalInstance}><App /></MsalProvider>
    : <main className="setup"><h1>Portal setup required</h1><p>Set the Entra portal application values before publishing this site.</p></main>);
}

bootstrap();
