import type { ReactNode } from "react";

interface WorkspaceProps {
  readonly eyebrow: string;
  readonly title: string;
  readonly description: string;
  readonly actions?: ReactNode;
  readonly children: ReactNode;
}

export function Workspace({
  eyebrow,
  title,
  description,
  actions,
  children,
}: WorkspaceProps) {
  return (
    <section className="workspace" aria-labelledby={`${title}-heading`}>
      <header className="workspace-header">
        <div>
          <p className="eyebrow">{eyebrow}</p>
          <h1 id={`${title}-heading`}>{title}</h1>
          <p className="workspace-description">{description}</p>
        </div>
        {actions ? <div className="workspace-actions">{actions}</div> : null}
      </header>
      {children}
    </section>
  );
}

export function EmptyStage({
  title,
  detail,
}: {
  readonly title: string;
  readonly detail: string;
}) {
  return (
    <div className="empty-stage">
      <strong>{title}</strong>
      <span>{detail}</span>
    </div>
  );
}
