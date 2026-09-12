export interface GitCommit {
  hash: string;
  short: string;
  date: string;
  subject: string;
  body: string;
  type: CommitType;
  isMerge: boolean;
}

export type CommitType =
  | 'feat'
  | 'fix'
  | 'refactor'
  | 'test'
  | 'docs'
  | 'merge'
  | 'chore'
  | 'other';

export interface Stage {
  id: string;
  name: string;
  description: string;
  startIndex: number;
  endIndex: number;
  commitCount: number;
  keyCommits: string[];
  color: string;
}
